using System.Text.RegularExpressions;
using OpenMcdf;
using RvtIO.Internal;
using RvtIO.Model;
using RvtIO.Parsing;

namespace RvtIO;

/// <summary>Main public API for reading Revit (.rvt) files.</summary>
public partial class RvtFileReader
{
    private readonly RvtReaderOptions _options;

    /// <summary>Creates a reader with the given options; null uses the defaults.</summary>
    public RvtFileReader(RvtReaderOptions? options = null)
    {
        _options = options ?? new RvtReaderOptions();
    }

    /// <summary>Diagnostic-only: the full <see cref="ParsingState"/> from the most recent
    /// <see cref="Read"/> call (unlike <see cref="ReadWithState"/>, which is a shortened path that skips the string scanner).
    /// Lets batch diagnostic tools read native-provenance maps that <see cref="RevitFileInfo"/> does not expose.
    /// Never read by production export code.</summary>
    internal ParsingState? LastParsingState { get; private set; }

    /// <summary>Read and parse a Revit file, returning structured element data.</summary>
    /// <exception cref="RvtInputException">The file cannot be read: missing, empty, not a compound file, cut or damaged.</exception>
    /// <exception cref="RvtLimitException">A stream is longer than one array can hold.</exception>
    public RevitFileInfo Read(string rvtPath)
    {
        RvtContainerCheck.Verify(rvtPath);
        Parsing.ElementBuilder.SeparateFacesWithDistinctOpenings = _options.SeparateFacesWithDistinctOpenings;
        var diag = _options.DiagnosticOutput;
        using var diagnosticScope = DiagnosticSink.Use(diag);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        void LogPhase(string name) { diag?.WriteLine($"[{sw.Elapsed.TotalSeconds:F1}s] {name}"); }

        using var root = RootStorage.OpenRead(rvtPath);

        var elemTable = ElemTableReader.Read(root, out var elemWorksets, out _, out bool elemTableIsExact,
            ElemTableRecover.Limits(_options.RaiseLimits), _options.Diagnostics);
        diag?.WriteLine($"ElemTable: {elemTable.Count} elements");

        var fileMetadata = BasicFileInfoReader.Read(root);
        diag?.WriteLine($"BasicFileInfo: {fileMetadata.Count} fields");

        var projectInfo = ProjectInfoReader.Read(root);
        diag?.WriteLine($"ProjectInfo: {projectInfo.Count} fields");

        var transmissionData = TransmissionDataReader.Read(root);
        diag?.WriteLine($"TransmissionData: {transmissionData?.ExternalReferences.Count ?? 0} external refs");

        var (previewBase64, previewMime) = PreviewImageReader.Read(root);
        if (previewBase64 != null)
            diag?.WriteLine($"Preview image: {previewMime} ({previewBase64.Length} base64 chars)");

        Dictionary<string, TfClassRegistry.TfClassDef>? tfSchemas = null;
        LogPhase("Decompressing partitions...");
        var chunks = DecompressPartitions(root, diag, out var ownPartitionWallParts, out var partitionStreams, out var framedStreams, out bool staleCopiesRemoved);
        var fmtChunks = DecompressStorage(root, "Formats", diag);
        if (fmtChunks.Count > 0)
        {
            chunks.AddRange(fmtChunks);
            // Parse Formats/Latest class schema table (stored temporarily, assigned to state below)
            try
            {
                tfSchemas = TfClassRegistry.Parse(fmtChunks[0]);
                diag?.WriteLine($"Tf class schemas: {tfSchemas.Count} classes, {tfSchemas.Values.Sum(c => c.Fields.Count)} fields");
            }
            // Schema table unreadable: continue without declared schemas.
            catch (Exception ex) when (MalformedInput.Is(ex)) { }
        }
        var globalChunks = DecompressStorage(root, "Global", diag);
        if (globalChunks.Count > 0) chunks.AddRange(globalChunks);

        // Episode GUID table: nested stream Global/History (there is no top-level "History" storage).
        var (elemRecHistories, elemRecOwners, wideSchema) = ReadElemRecs(root);
        LogRelease(root, fileMetadata, wideSchema, diag);
        var episodeTable = ReadEpisodeTable(root, diag, wideSchema);
        bool r22View = false;
        IReadOnlyList<PartitionFraming.Framed?> readFrames = framedStreams;
        int partitionChunkCount = partitionStreams.Sum(s => s.Count);
        if (wideSchema != null && _options.R22Layout
            && RewriteToR22Layout(root, wideSchema, chunks, partitionStreams, framedStreams, diag) is { } r22)
        {
            (chunks, partitionChunkCount, var r22Frames) = r22;
            readFrames = r22Frames;
            r22View = true;
        }
        diag?.WriteLine($"ElemRec histories: {(elemRecHistories != null ? $"{elemRecHistories.Count} (exact)" : "unavailable")}");

        LogPhase("Decompression done");
        var state = new ParsingState
        {
            ElemTable = elemTable,
            ElemWorksets = elemWorksets,
            AllKnownIds = new HashSet<uint>(elemTable.Keys),
            Chunks = chunks,
            OwnPartitionWallParts = ownPartitionWallParts,
            TfClassSchemas = tfSchemas ?? new(),
            LocalUsername = fileMetadata.GetValueOrDefault("Username"),
            EpisodeGuids = episodeTable.Guids,
            EpisodeTableX = episodeTable.X,
            HistoryEpisodeGuids = episodeTable.Guids,
            DocumentDetachGuid = episodeTable.DetachGuid,
            ElemRecHistories = elemRecHistories,
            ElemRecOwningElementIds = elemRecOwners,
            R22View = r22View,
            StaleCopiesRemoved = staleCopiesRemoved,
            FramedPartitions = readFrames,
            PartitionChunkCount = partitionChunkCount,
        };
        ReadWideElementIndex(root, state, partitionStreams, framedStreams, wideSchema, diag);
        ReadRebarBars(root, state, partitionStreams, framedStreams, wideSchema);
        ClassifyAndParseRecords(chunks, state, diag);
        LogPhase("Records parsed");

        ParseBlocks(state, diag);
        LogPhase("Blocks parsed");

        // Continuous-stream anchored walk: blocks cross chunk boundaries, so this sees many more blocks than the
        // per-chunk walk. Extracts native parameter stores and re-runs the key block-driven signals at full coverage.
        RunTfStreamWalk(state);
        if (state.WideSchema == null) ReadOwnDocumentUnits(root, state, diag);
        LogPhase("Tf stream walked");
        diag?.WriteLine($"Grid datums: {state.GridDatums.Count}");

        // Scan all chunks for element name strings in parallel. Store each result at its
        // source chunk index so task completion order cannot change first-match name resolution.
        var partialResults = new Dictionary<uint, List<string>>?[chunks.Count];
        Parallel.For(0, chunks.Count, chunkIndex =>
        {
            var localStrings = new Dictionary<uint, List<string>>();
            StringScanner.ScanChunk(chunks[chunkIndex], state.AllKnownIds, localStrings);
            if (localStrings.Count > 0) partialResults[chunkIndex] = localStrings;
        });
        MergeElemScanStringsInChunkOrder(partialResults, state.ElemScanStrings);
        LogPhase("Strings scanned");
        diag?.WriteLine($"Elements with BIP doubles: {state.ElemBipDoubles.Count}");

        WallWidthExtractor.ExtractFromBlocks(state);
        WallWidthExtractor.ExtractFromGrid(state);
        WallWidthExtractor.ExtractFromBlockGrid(state);

        // Optional text dump of the model
        if (_options.ExternalDumpPath != null)
            ExternalDumpParser.Parse(_options.ExternalDumpPath, state, diag, _options.ExternalDumpClassPrefix);

        ExtractForgeParamDefs(state);
        diag?.WriteLine($"Forge param defs: {state.ForgeParamDefs.Count}, docGuid: {state.DocGuid ?? "not found"}");

        int paramDefsWithName = state.ForgeParamDefs.Values.Count(d => d.DisplayName != null);

        // Extract binary GUIDs from the elem-to-GUID table in Global/Latest chunks
        // Format: consecutive [elemId:4][GUID:16] = 20-byte entries
        ExtractBinaryParamGuids(state, diag);
        diag?.WriteLine($"  Param defs with name: {paramDefsWithName}/{state.ForgeParamDefs.Count}");

        // Extract param captions from binary data (no dump needed)
        int rawCaptionApplied = 0;

        byte[] forgeMarker = System.Text.Encoding.Unicode.GetBytes("autodesk.parameter.aec.revit.");
        var forgeIdHits = new List<(int chunkIdx, int offset, string forgeId, uint parsedElemId)>();
        for (int ci = 0; ci < state.Chunks.Count; ci++)
        {
            var chunk = state.Chunks[ci];
            for (int j = 5; j + forgeMarker.Length <= chunk.Length; j++)
            {
                if (chunk[j] != 0x61 || chunk[j + 1] != 0x00) continue; // 'a'
                bool m2 = true;
                for (int x = 0; x < forgeMarker.Length && m2; x++)
                    if (chunk[j + x] != forgeMarker[x]) m2 = false;
                if (!m2) continue;

                var sb = new System.Text.StringBuilder();
                for (int k = 0; j + k * 2 + 1 < chunk.Length; k++)
                {
                    byte lo = chunk[j + k * 2], hi = chunk[j + k * 2 + 1];
                    // Allow Deflate64 artifact bytes (0x1B, 0xF7) in high byte
                    if (hi != 0 && hi != 0x1B && hi != 0xF7) break;
                    char c = Parsing.StringScanner.DecodeUtf16Char(lo, hi);
                    if (c < 0x20 || c > 0x7E) break;
                    sb.Append(c);
                    if (sb.Length > 200) break;
                }
                string fid = sb.ToString();
                if (fid.Length < 30 || !fid.StartsWith("autodesk.parameter.aec.revit.")) continue;
                int sLenOff = j;
                for (int tryOff = -3; tryOff >= -6; tryOff--)
                {
                    if (j + tryOff < 0) continue;
                    uint tryLen = BitConverter.ToUInt32(chunk, j + tryOff);
                    if (tryLen == (uint)fid.Length) { sLenOff = j + tryOff; break; }
                }

                uint parsedId = 0;
                if (fid.Contains("revit.family:"))
                {
                    var part = fid.Split("family:").LastOrDefault()?.Split('-').FirstOrDefault();
                    if (part != null) uint.TryParse(part, out parsedId);
                }
                forgeIdHits.Add((ci, sLenOff, fid, parsedId));
                j += fid.Length * 2;
            }
        }
        int familyHits = forgeIdHits.Count(h => h.forgeId.Contains("family:"));
        int externalHits = forgeIdHits.Count(h => h.forgeId.Contains("external."));
        diag?.WriteLine($"  ForgeId scan: {forgeIdHits.Count} ({familyHits} family, {externalHits} external)");

        // Layout: [CaptionLen:4][Caption][ParamElemId:i64][bools:3][ForgeIdLen:4][ForgeId]
        // So ForgeId len is at offset (forgeStart), and ParamElemId is at (forgeStart - 11)
        int captionsFound = 0;
        foreach (var (ci, offset, forgeId, parsedElemId) in forgeIdHits)
        {
            var chunk = state.Chunks[ci];
            int forgeStart = offset; // ForgeId length field offset (lenOff stored in offset field)

            // Layout before ForgeId: [ParamElemId:8][IsReadOnly:1][IsUserVisible:1][IsAllowVary:1]
            // = 11 bytes before ForgeId length field
            uint resolvedElemId = parsedElemId; // from ForgeId text (family params)
            if (resolvedElemId == 0)
            {
                // For external params the elemId is AFTER the ForgeId string: scan both forward and backward from it
                int forgeEnd = forgeStart + 4 + forgeId.Length * 2;
                for (int tryOff = forgeEnd; tryOff < Math.Min(forgeEnd + 2000, chunk.Length - 4); tryOff++)
                {
                    uint candidate = BitConverter.ToUInt32(chunk, tryOff);
                    if (candidate > 100 && state.AllKnownIds.Contains(candidate)
                        && state.IsParameterElement(candidate))
                    { resolvedElemId = candidate; break; }
                }
                // Backward scan: collect ALL ParamElem IDs within 500 bytes, pick the unnamed one
                if (resolvedElemId == 0)
                {
                    var candidates = new List<(uint eid, int dist)>();
                    for (int tryOff = forgeStart - 4; tryOff >= Math.Max(0, forgeStart - 500); tryOff--)
                    {
                        uint c = BitConverter.ToUInt32(chunk, tryOff);
                        if (c > 100 && state.AllKnownIds.Contains(c)
                            && state.IsParameterElement(c)
                            && !candidates.Any(x => x.eid == c))
                            candidates.Add((c, forgeStart - tryOff));
                    }
                    // Prefer: unnamed param closest to ForgeId
                    var unnamed = candidates.FirstOrDefault(x =>
                        !state.ForgeParamDefs.TryGetValue(x.eid, out var ex7) || ex7.DisplayName == null);
                    if (unnamed.eid > 0) resolvedElemId = unnamed.eid;
                    else if (candidates.Count > 0) resolvedElemId = candidates[0].eid;
                }
            }
            if (resolvedElemId == 0) continue;
            if (state.ForgeParamDefs.TryGetValue(resolvedElemId, out var ex) && ex.DisplayName != null) continue;

            // Find Caption before ForgeId: scan for U+2026 (bytes 26 20) or & (26 00) or a Cyrillic letter start
            // within 500 bytes before forgeStart, then read until non-printable
            for (int back = forgeStart - 10; back >= Math.Max(0, forgeStart - 500); back -= 2)
            {
                byte clo = chunk[back], chi = chunk[back + 1];
                // Caption start: U+2026 (bytes 26 20) for external, or ASCII/Cyrillic letter for family
                bool isEllipsis = (clo == 0x26 && chi == 0x20);
                bool isAmpersand = (clo == 0x26 && chi == 0x00);
                // For non-prefixed captions: require that char BEFORE is non-printable (end of previous field)
                bool isCyrStart = chi == 0x04 && back >= 2 && chunk[back - 1] == 0x00 && chunk[back - 2] == 0x00;
                bool isAsciiStart = chi == 0x00 && clo >= 'A' && clo <= 'z' && back >= 2 && chunk[back - 1] == 0x00 && chunk[back - 2] == 0x00;
                if (!isEllipsis && !isAmpersand && !isCyrStart && !isAsciiStart) continue;
                int capLen = 0;
                for (int k = 0; back + k * 2 + 1 < chunk.Length && back + k * 2 < forgeStart; k++)
                {
                    byte lo2 = chunk[back + k * 2], hi2 = chunk[back + k * 2 + 1];
                    if (hi2 == 0x04 || hi2 == 0x20 || (hi2 == 0 && lo2 >= 0x20 && lo2 < 0x7F) || hi2 == 0x1B || hi2 == 0xF7)
                        capLen = k + 1;
                    else break;
                }
                if (capLen < 3) continue;

                var cSb = new System.Text.StringBuilder();
                for (int k = 0; k < capLen; k++)
                {
                    byte lo = chunk[back + k * 2], hi = chunk[back + k * 2 + 1];
                    cSb.Append(Parsing.StringScanner.DecodeUtf16Char(lo, hi));
                }
                string caption = cSb.ToString().Trim();
                if (caption.Length > 0 && (caption[0] == '\u2026' || caption[0] == '&')) caption = caption[1..];
                if (caption.Length < 3 || caption.StartsWith("autodesk.")) continue;

                state.ForgeParamDefs[resolvedElemId] = (forgeId, caption);
                rawCaptionApplied++;
                captionsFound++;
                break;
            }
        }
        diag?.WriteLine($"  ForgeId to Caption: {captionsFound} new ({forgeIdHits.Count} ForgeIds found)");

        // Cross-reference: family ForgeIds may use internal IDs (e.g. family:558399) that differ from
        // document ParamElem IDs. Build a ForgeId-to-Caption lookup, then match document elements
        // that have the same ForgeId in their ElemStrings.
        var forgeIdToCaption = new Dictionary<string, string>();
        foreach (var (eid, def) in state.ForgeParamDefs)
        {
            if (!string.IsNullOrEmpty(def.ForgeId) && !string.IsNullOrEmpty(def.DisplayName))
                forgeIdToCaption.TryAdd(def.ForgeId, def.DisplayName);
        }
        int crossRefApplied = 0;
        foreach (var (eid, strs) in state.ElemStrings)
        {
            if (state.ForgeParamDefs.TryGetValue(eid, out var existing) && existing.DisplayName != null) continue;
            foreach (var s in strs)
            {
                if (s.StartsWith("autodesk.parameter.") && forgeIdToCaption.TryGetValue(s, out var caption))
                {
                    state.ForgeParamDefs[eid] = (s, caption);
                    crossRefApplied++;
                    break;
                }
            }
        }
        foreach (var (eid, strs) in state.ElemScanStrings)
        {
            if (state.ForgeParamDefs.TryGetValue(eid, out var existing) && existing.DisplayName != null) continue;
            foreach (var s in strs)
            {
                if (s.StartsWith("autodesk.parameter.") && forgeIdToCaption.TryGetValue(s, out var caption))
                {
                    state.ForgeParamDefs[eid] = (s, caption);
                    crossRefApplied++;
                    break;
                }
            }
        }
        if (crossRefApplied > 0) diag?.WriteLine($"  ForgeId cross-ref: {crossRefApplied} additional names");

        // Family param mapping: for unnamed ParamElems, extract FamId from the record body, then find their
        // ForgeId in the raw chunk data near the family's data region.
        // Record body Tf base fields: after the entries sentinel (0xFFFF), FamId is at offset +4.
        foreach (var eid in state.ElemTable.Keys)
        {
            if (!state.IsParameterElement(eid)) continue;
            if (state.ForgeParamDefs.TryGetValue(eid, out var existing) && existing.DisplayName != null) continue;
            if (!state.Records.TryGetValue(eid, out var rec)) continue;
            var body = rec.Body;
            int famId = -1;
            for (int p = 2; p + 2 <= body.Length - 10; p += 14)
            {
                ushort classId = BitConverter.ToUInt16(body, p + 10);
                if (classId == 0xFFFF || classId == 0)
                {
                    // Tf base fields start here: [assocLevelId:4][famId:4]
                    int tfStart = p + 14; // after sentinel entry
                    if (tfStart + 8 <= body.Length)
                    {
                        famId = BitConverter.ToInt32(body, tfStart + 4);
                    }
                    break;
                }
            }
            if (famId <= 0) continue;
            // A ParamElem with a FamId is found here, but its ForgeId cannot be derived from the record (the "family:X" id is
            // family-internal); the caption comes from the raw ForgeId scan.
        }

        // Method 2: FFFFFFFF+GUID+Caption scan in raw chunk regions (for External params)
        ExtractExternalParamCaptionsFromRaw(state, ref rawCaptionApplied, diag);

        // Method 3: Tf ParamDef structure parsing - find m_paramElemId after ForgeId+GroupTypeId
        // Layout: [Caption][FFFFFFFF+hdr+GUID][ForgeId][GroupTypeId?][m_paramElemId:int64/int32]
        // After ForgeId string end (+Deflate artifacts): scan for known ParamElem elemId within 20 bytes
        int tfParsed = 0;
        foreach (var (ci4, off4, fid4, pid4) in forgeIdHits)
        {
            if (pid4 > 0) continue; // family params resolved by ForgeId text
            var ch4 = state.Chunks[ci4];
            // Find actual ForgeId end (with Deflate64 artifact tolerance)
            int fStart = -1;
            for (int t = off4; t <= off4 + 8 && t + 4 < ch4.Length; t++)
                if (ch4[t] == 0x61 && ch4[t+1] == 0x00 && ch4[t+2] == 0x75 && ch4[t+3] == 0x00)
                { fStart = t; break; }
            if (fStart < 0) continue;
            int fEnd = fStart;
            for (int k = 0; fStart + k * 2 + 1 < ch4.Length; k++)
            {
                byte lo = ch4[fStart + k * 2], hi = ch4[fStart + k * 2 + 1];
                if (hi != 0 && hi != 0x1B && hi != 0xF7) break;
                char c = Parsing.StringScanner.DecodeUtf16Char(lo, hi);
                if (c < 0x20 || c > 0x7E) break;
                fEnd = fStart + (k + 1) * 2;
            }
            uint resolved = 0;
            for (int tryOff = fEnd; tryOff < Math.Min(fEnd + 200, ch4.Length - 4); tryOff++)
            {
                uint c5 = BitConverter.ToUInt32(ch4, tryOff);
                if (c5 > 100 && state.AllKnownIds.Contains(c5)
                    && state.IsParameterElement(c5))
                { resolved = c5; break; }
            }
            if (resolved == 0) continue;
            if (state.ForgeParamDefs.TryGetValue(resolved, out var ex8) && ex8.DisplayName != null)
            { continue; }

            // Read Caption before ForgeId (before FFFFFFFF+GUID+0001+captionLen)
            // The ForgeId len field is at fStart - 3..6 bytes. Caption is before that.
            // Caption is before ForgeId in CDR stream. Try multiple len-field offsets.
            for (int lenTry = -3; lenTry >= -8; lenTry--)
            {
                int back = fStart + lenTry;
                for (; back >= Math.Max(0, fStart - 500); back--)
                {
                    if (back + 4 > ch4.Length) continue;
                    uint capLen = BitConverter.ToUInt32(ch4, back);
                    if (capLen < 3 || capLen > 100) continue;
                    if (back + 4 + (int)capLen * 2 > ch4.Length) continue;
                    int capEnd = back + 4 + (int)capLen * 2;
                    int gap = fStart - capEnd;
                    if (gap < -2 || gap > 40) continue;
                var cSb = new System.Text.StringBuilder();
                bool cValid = true, hasPrint = false;
                for (int k = 0; k < (int)capLen && cValid; k++)
                {
                    byte lo = ch4[back + 4 + k * 2], hi = ch4[back + 4 + k * 2 + 1];
                    char c = Parsing.StringScanner.DecodeUtf16Char(lo, hi);
                    if (hi == 0x04 || hi == 0x20 || hi == 0x1B || hi == 0xF7) hasPrint = true;
                    else if (hi == 0x00 && lo >= 0x20 && lo < 0x7F) { if (char.IsLetterOrDigit((char)lo)) hasPrint = true; }
                    else { cValid = false; }
                    cSb.Append(c);
                }
                if (!cValid || !hasPrint) continue;
                string raw = cSb.ToString().Trim();
                if (raw.Length > 0 && (raw[0] == '\u2026' || raw[0] == '&')) raw = raw[1..];
                if (raw.Length < 1 || raw.StartsWith("autodesk.")) continue;
                state.ForgeParamDefs[resolved] = (fid4, raw);
                tfParsed++;
                goto nextTfHit;
            }
            }
            nextTfHit:;
        }
        diag?.WriteLine($"  Tf m_paramElemId: {tfParsed} names (from {forgeIdHits.Count(h => h.parsedElemId == 0)} external hits)");
        int fpdNamed = state.ForgeParamDefs.Values.Count(d => d.DisplayName != null);
        diag?.WriteLine($"  Param defs with name: {fpdNamed}/{state.ForgeParamDefs.Count}");

        // Method 4: Parse Caption from ParamElem record bodies (for bodies > 87 bytes)
        int bodyCaption = 0;
        foreach (var eid in state.ElemTable.Keys)
        {
            if (!state.IsParameterElement(eid)) continue;
            if (state.ForgeParamDefs.TryGetValue(eid, out var ex4) && ex4.DisplayName != null) continue;
            if (!state.Records.TryGetValue(eid, out var rec)) continue;
            var body = rec.Body;
            if (body.Length <= 87) continue; // minimal body has no strings

            for (int j = 10; j + 40 <= body.Length; j++)
            {
                if (body[j] != 0x61 || body[j + 1] != 0x00) continue; // 'a\0'
                if (j + 4 >= body.Length || body[j + 2] != 0x75 || body[j + 3] != 0x00) continue;
                var fsb = new System.Text.StringBuilder();
                for (int k = 0; j + k * 2 + 1 < body.Length; k++)
                {
                    byte lo = body[j + k * 2], hi = body[j + k * 2 + 1];
                    if (hi != 0 || lo < 0x20 || lo > 0x7E) break;
                    fsb.Append((char)lo);
                    if (fsb.Length > 200) break;
                }
                string fid = fsb.ToString();
                if (!fid.StartsWith("autodesk.parameter.")) continue;

                int forgeOff = j;
                for (int back = forgeOff - 2; back >= Math.Max(0, forgeOff - 500); back--)
                {
                    if (back + 4 > body.Length) continue;
                    uint capLen = BitConverter.ToUInt32(body, back);
                    if (capLen < 1 || capLen > 200) continue;
                    int capEnd = back + 4 + (int)capLen * 2;
                    int gap = forgeOff - capEnd;
                    if (gap < -6 || gap > 10) continue; // allow slight overlap for len field
                    // Try len field at different offsets
                    for (int lenTry = -3; lenTry <= 0; lenTry++)
                    {
                        int tryBack = back + lenTry;
                        if (tryBack < 0 || tryBack + 4 > body.Length) continue;
                        uint tryLen = BitConverter.ToUInt32(body, tryBack);
                        if (tryLen < 1 || tryLen > 200) continue;
                        int tryEnd = tryBack + 4 + (int)tryLen * 2;
                        int tryGap = forgeOff - tryEnd;
                        if (tryGap < 0 || tryGap > 10) continue;
                        capLen = tryLen;
                        back = tryBack;
                        break;
                    }
                    capEnd = back + 4 + (int)capLen * 2;
                    if (forgeOff - capEnd < 0 || forgeOff - capEnd > 10) continue;

                    var cSb = new System.Text.StringBuilder();
                    bool cValid = true, hasPrint = false;
                    for (int k = 0; k < (int)capLen && cValid; k++)
                    {
                        byte lo = body[back + 4 + k * 2], hi = body[back + 4 + k * 2 + 1];
                        if (hi == 0x04) { cSb.Append((char)(0x0400 | lo)); hasPrint = true; }
                        else if (hi == 0x20) { cSb.Append((char)(0x2000 | lo)); hasPrint = true; }
                        else if (hi == 0x00 && lo >= 0x20 && lo < 0x7F) { cSb.Append((char)lo); if (char.IsLetterOrDigit((char)lo)) hasPrint = true; }
                        else { cValid = false; }
                    }
                    if (!cValid || !hasPrint) continue;
                    string caption = cSb.ToString().Trim();
                    if (caption.Length > 0 && (caption[0] == '\u2026' || caption[0] == '&')) caption = caption[1..];
                    if (caption.Length < 1 || caption.StartsWith("autodesk.")) continue;

                    state.ForgeParamDefs[eid] = (fid, caption);
                    bodyCaption++;
                    break;
                }
                break;
            }
        }
        diag?.WriteLine($"  Body caption scan: {bodyCaption} names from record bodies");

        // ForgeIds in 0x0725 blocks belong to specific families: each 0x0725 block lists the ForgeIds of its family's parameters.
        // Match the block's elemId to its family, then to the document ParamElem elements with the same FamId, and pair
        // ForgeIds with ParamElems by their order within the family.
        {
            // Collect ForgeIds per 0x0725 block element
            var familyForgeIds = new Dictionary<uint, List<string>>(); // 0x0725 elemId to ForgeIds
            foreach (var chunk in state.NonRecordChunks)
            {
                int pos = 0;
                while (pos + 16 <= chunk.Length)
                {
                    uint beid = BitConverter.ToUInt32(chunk, pos);
                    int bsz = BitConverter.ToInt32(chunk, pos + 8);
                    uint bci = BitConverter.ToUInt32(chunk, pos + 12);
                    if (bsz < 0 || bsz > chunk.Length - pos - 16) break;
                    if ((bci & 0xFFFF) == 0x0725 && bsz > 100 && state.AllKnownIds.Contains(beid))
                    {
                        int dstart = pos + 16, dend = dstart + bsz;
                        var forgeList = new List<string>();
                        for (int j = dstart; j + 40 <= dend; j++)
                        {
                            if (chunk[j] != 0x61 || chunk[j + 1] != 0x00) continue;
                            var fsb = new System.Text.StringBuilder();
                            for (int k = 0; j + k * 2 + 1 < dend; k++)
                            {
                                byte lo = chunk[j + k * 2], hi = chunk[j + k * 2 + 1];
                                if (hi != 0 || lo < 0x20 || lo > 0x7E) break;
                                fsb.Append((char)lo);
                                if (fsb.Length > 200) break;
                            }
                            string fid = fsb.ToString();
                            if (fid.StartsWith("autodesk.parameter.aec.revit.family:"))
                            { forgeList.Add(fid); j += fid.Length * 2; }
                        }
                        if (forgeList.Count > 0)
                            familyForgeIds[beid] = forgeList;
                    }
                    pos += 16 + bsz;
                }
            }

            // For each family (0x0725 block owner), find the document ParamElems with the same owner/family ref:
            // the block's References should include the ParamElem IDs.
            int cdrMapped = 0;
            foreach (var (famBlockEid, forgeList) in familyForgeIds)
            {
                if (!state.Records.TryGetValue(famBlockEid, out var famRec)) continue;
                // Collect references with one validated own ParamElem root.
                var paramElemRefs = new List<uint>();
                foreach (var refId in famRec.References)
                {
                    if (state.IsParameterElement(refId))
                        paramElemRefs.Add(refId);
                }
                if (paramElemRefs.Count == 0) continue;

                // Pair forgeList[i] with paramElemRefs[i] by order. ForgeIds contain family-internal IDs, ParamElems are document IDs.
                // If the counts match this is a direct mapping; otherwise family ForgeIds are sorted by internal ID, so extract the
                // internal ID from each ForgeId, sort, and pair with the sorted paramElemRefs.
                var familyPairs = new List<(uint internalId, string forgeId)>();
                foreach (var fid in forgeList)
                {
                    var idPart = fid.Split("family:").LastOrDefault()?.Split('-').FirstOrDefault();
                    if (idPart != null && uint.TryParse(idPart, out uint intId))
                        familyPairs.Add((intId, fid));
                }
                familyPairs.Sort((a, b) => a.internalId.CompareTo(b.internalId));
                var sortedParamElems = paramElemRefs.OrderBy(id => id).ToList();

                if (familyPairs.Count > 0 && sortedParamElems.Count > 0)
                    diag?.WriteLine($"    famBlock={famBlockEid}: {familyPairs.Count} ForgeIds, {sortedParamElems.Count} ParamElem refs");
                if (familyPairs.Count == sortedParamElems.Count && familyPairs.Count > 0)
                {
                    // Perfect match: pair by sorted order
                    for (int k = 0; k < familyPairs.Count; k++)
                    {
                        uint docEid = sortedParamElems[k];
                        string fid = familyPairs[k].forgeId;
                        if (state.ForgeParamDefs.TryGetValue(docEid, out var ex5) && ex5.DisplayName != null) continue;
                        if (forgeIdToCaption.TryGetValue(fid, out var cap))
                        {
                            state.ForgeParamDefs[docEid] = (fid, cap);
                            cdrMapped++;
                        }
                    }
                }
            }
            if (cdrMapped > 0) diag?.WriteLine($"  0x0725 family to ParamElem pairing: {cdrMapped} names");
        }

        LogPhase("Building output");
        var rvtInfo = new RevitFileInfo
        {
            ElemTableIsExact = elemTableIsExact,
            GeometryUndecoded = state.WideSchema != null,
            WideLayoutNotRewritten = state.WideSchema != null && !state.R22View,
            Metadata = new FileMetadata
            {
                StreamVersion = fileMetadata.GetValueOrDefault("StreamVersion"),
                RevitVersion = fileMetadata.GetValueOrDefault("RevitVersion"),
                Build = fileMetadata.GetValueOrDefault("Build"),
                LastSavePath = fileMetadata.GetValueOrDefault("LastSavePath"),
                SavedLocale = fileMetadata.GetValueOrDefault("Locale when saved"),
                PreviewImage = previewBase64,
                PreviewImageMimeType = previewMime,
            },
            ProjectInfo = new ProjectInfo
            {
                Name = projectInfo.GetValueOrDefault("Name"),
                Number = projectInfo.GetValueOrDefault("Number"),
                Client = projectInfo.GetValueOrDefault("Client"),
                Address = projectInfo.GetValueOrDefault("Address"),
                Status = projectInfo.GetValueOrDefault("Status"),
                IssueDate = projectInfo.GetValueOrDefault("IssueDate"),
                Title = projectInfo.GetValueOrDefault("Title"),
                RevitVersion = projectInfo.GetValueOrDefault("RevitVersion"),
                Author = projectInfo.GetValueOrDefault("Author"),
                BuildingName = projectInfo.GetValueOrDefault("BuildingName"),
                OrganizationName = projectInfo.GetValueOrDefault("OrganizationName"),
            },
            TransmissionData = transmissionData,
        };

        diag?.WriteLine($"Family symbol type-level extraction: {state.FamilySymbolBlockDoubles.Count} symbols with doubles, {state.FamilySymbolBlockStrings.Count} with strings");
        diag?.WriteLine($"Family complex cutters: {state.FamilyCutGreps.Count} symbols, " +
            $"{state.FamilyCutGreps.Values.Sum(cut => cut.Faces.Count)} faces, " +
            $"{state.FamilyCutGreps.Values.Sum(cut => cut.Solids.Count(solid => solid.IsClosed))} closed solids");
        // Remap instance ElemParamStrings keys via the family symbol's 0x725 mapping table.
        // Binary 0x702 blocks store values keyed by family-local "bin paramKeys"; the actual
        // project param def IDs come from the family symbol's parameter linked-list.
        RemapInstanceParamKeys(state, diag);

        // Pre-propagate BIP doubles through reference chains
        PrePropagateBipDoubles(state, diag);

        LogPhase("Parameter keys remapped");
        // a file with 64-bit ids lists the elements of its element table only (the record and stream heuristics also find ids of the
        // loaded families' own documents)
        var extraRecordIds = state.WideSchema != null ? new List<uint>() : state.Records.Keys
            .Where(k => !state.ElemTable.ContainsKey(k))
            .OrderBy(k => k).ToList();

        foreach (var (elemId, typeId) in state.ElemTable.OrderBy(x => x.Key))
            ElementBuilder.BuildElemTableElement(elemId, typeId, state, rvtInfo);

        LogPhase("Elements built");
        foreach (var eid in extraRecordIds)
            ElementBuilder.BuildDiscoveredElement(eid, state, rvtInfo);
        LogPhase("Discovered elements built");

        // GRep blocks discovered by the stream walk / targeted scan that no index knows:
        // real geometry whose Record/ElemTable entries sit in unrecoverable stream members
        // (e.g. heavy slabs with thousands of reference triangles were entirely absent).
        // Referenced family symbols are retained as block-only definition elements. They
        // are not exported as standalone IFC products, but callers need their geometry to
        // resolve GeometryData.Instances without reaching into parser internals.
        var referencedSyms = new HashSet<uint>();
        foreach (var g in state.GrepBlocks.Values)
            foreach (var instance in g.Instances)
                if (instance.SymbolElemId > 0) referencedSyms.Add((uint)instance.SymbolElemId);
        foreach (var kv in state.GrepBlocks)
            if ((state.WideSchema == null || state.R22View) && (kv.Value.Faces.Count >= 4 || referencedSyms.Contains(kv.Key))
                && !state.ElemTable.ContainsKey(kv.Key)
                && !state.Records.ContainsKey(kv.Key))
                state.BlockOnlyIds.Add(kv.Key);
        // FamilySymbol.m_complexCutGeoms can be the only decoded block for a definition.
        // Keep that definition in the model so an instance's exact serialized SymbolId can
        // resolve the cutter without relying on category/name/reference inference.
        foreach (uint familySymbolId in state.FamilyCutGreps.Keys)
            if (!state.ElemTable.ContainsKey(familySymbolId)
                && !state.Records.ContainsKey(familySymbolId))
                state.BlockOnlyIds.Add(familySymbolId);
        foreach (uint familySymbolId in state.FamilyCutLoops.Keys)
            if (!state.ElemTable.ContainsKey(familySymbolId)
                && !state.Records.ContainsKey(familySymbolId))
                state.BlockOnlyIds.Add(familySymbolId);

        foreach (var bid in state.BlockOnlyIds)
            ElementBuilder.BuildBlockOnlyElement(bid, state, rvtInfo);

        // Wall function extraction: scan all chunks for classIdx=0x00012100 blocks
        ExtractWallFunctions(state);
        diag?.WriteLine($"Wall type functions: {state.WallTypeFunctions.Count} entries");

        LogPhase("Block-only elements built");
        ElementBuilder.PostProcess(rvtInfo, state, diag);
        LogPhase("Post-processed");
        ElementBuilder.ApplyValidatedStairComponentTypeNames(rvtInfo.Elements, state);
        ElementBuilder.ApplyValidatedCorniceTypeNames(rvtInfo.Elements, state);
        ElementBuilder.ApplyValidatedDuctTypeNames(rvtInfo.Elements, state);
        ElementBuilder.ApplyValidatedMepTypeNames(rvtInfo.Elements, state);
        ElementBuilder.ApplyMepCurveTypeLinks(rvtInfo.Elements, state);
        diag?.WriteLine($"MEP curve types: occurrences with an exact type link={rvtInfo.Elements.Count(e => e.SerializedMepCurveTypeId != null)}, "
            + $"link differs from the general type={rvtInfo.Elements.Count(e => e.SerializedMepCurveTypeId != null && e.SerializedMepCurveTypeId != e.FamilyTypeId)}, "
            + $"types with an own name={rvtInfo.Elements.Count(e => e.ValidatedMepTypeName != null)}");
        string? systemFamilyLocale = _options.SystemFamilyNameLocale ?? rvtInfo.Metadata.SavedLocale;
        var systemFamilyNames = Lookup.RevitSystemFamilyNames.Load(rvtInfo.Metadata.RevitVersion, systemFamilyLocale);
        // an earlier release has no embedded names: its table holds the English names of the system families the reader names itself (curtain
        // wall, walls, roofs, ceilings, railings, slabs, stairs and their runs, stringers and carriages, MEP curves), and only the type roots
        // of those families read them; the MEP curve names follow the locale in effect, the others only a locale a profile requests, so a
        // reader without a requested locale (the legacy profile) is unchanged
        var familyNames = systemFamilyNames
            ?? Lookup.RevitSystemFamilyNames.LoadPreviousReleaseFamilies(rvtInfo.Metadata.RevitVersion, systemFamilyLocale, _options.SystemFamilyNameLocale);
        int systemFamilyTypes = ElementBuilder.ApplyValidatedSystemFamilyNames(rvtInfo.Elements, state, familyNames);
        // the stored lookup entries are in the language the file was saved in, whatever locale the exported names use
        rvtInfo.SerializedLookupNoneText = Lookup.RevitSystemFamilyNames.Load(
            rvtInfo.Metadata.RevitVersion, rvtInfo.Metadata.SavedLocale)?.Get(Lookup.RevitUiText.LookupNone);
        ElementBuilder.ApplyValidatedStairOccurrenceNames(rvtInfo.Elements, familyNames);
        ElementBuilder.ApplyHostLabels(rvtInfo, systemFamilyNames, state.ElemClassIdx);
        ElementBuilder.ApplyWorkPlaneLabels(rvtInfo, systemFamilyNames);
        ElementBuilder.ApplyColumnLocationMarks(rvtInfo, state.OwnDocumentUnitOptions?.Value, state.ElemClassIdx);
        ElementBuilder.ApplyMepSystemParams(rvtInfo, state, systemFamilyNames);
        ElementBuilder.ApplyConnectorGraph(rvtInfo, state);
        ElementBuilder.ApplyStairNameParameterScopes(rvtInfo.Elements, state);
        diag?.WriteLine($"System family names: locale={rvtInfo.Metadata.SavedLocale ?? "?"}, " +
            $"resources={(systemFamilyNames != null ? "loaded" : "unavailable")}, types={systemFamilyTypes}");
        LogPhase("Names and parameters applied");
        int wallTopAttachments = ElementBuilder.ApplyWallJoinedToTop(rvtInfo.Elements, state);
        diag?.WriteLine($"Wall top attachment: roots={state.WallJoinedToTop.Count}, "
            + $"duplicate={state.WallJoinedToTop.Values.Count(v => v.BlockCount != 1)}, "
            + $"unread={state.WallJoinedToTop.Values.Count(v => v.BlockCount == 1 && v.JoinedToTop == null)}, "
            + $"top={state.WallJoinedToTop.Values.Count(v => v.BlockCount == 1 && v.JoinedToTop == true)}, "
            + $"walls={wallTopAttachments}");
        int wallEndJoins = ElementBuilder.ApplyWallEndJoins(rvtInfo.Elements, state);
        diag?.WriteLine($"Wall end joins: roots={state.WallEndJoins.Count}, "
            + $"duplicate={state.WallEndJoins.Values.Count(v => v.BlockCount != 1)}, "
            + $"unread={state.WallEndJoins.Values.Count(v => v.BlockCount == 1 && v.Joins == null)}, walls={wallEndJoins}"
            + (state.WallEndJoinFailures.Count > 0
                ? "; unread by reason: " + string.Join(", ", state.WallEndJoinFailures
                    .OrderByDescending(entry => entry.Value).Take(8).Select(entry => $"{entry.Key}={entry.Value}"))
                : ""));
        var (namedMaterials, ceilingTypes) = ElementBuilder.ApplyCeilingFinishSource(rvtInfo.Elements, state);
        diag?.WriteLine($"Ceiling finish source: materials={namedMaterials}/{state.MaterialNames.Count}, "
            + $"ceiling types={ceilingTypes}/{state.CeilingCompoundStructureLayerSource.Count}");
        int hostLayerTypes = ElementBuilder.ApplyHostTypeCompoundStructures(rvtInfo.Elements, state);
        diag?.WriteLine($"Host type compound structures: types={hostLayerTypes}/{state.HostTypeCompoundStructures.Count}");
        int ceilingThicknessTypes = ElementBuilder.ApplyCompoundCeilingTypeThickness(rvtInfo.Elements, state);
        diag?.WriteLine($"Own computed ceiling thickness: types={ceilingThicknessTypes}/{state.CompoundCeilingTypeThicknessFeet.Count}");
        int wallThicknessOccurrences = ElementBuilder.ApplyBasicWallTypeThickness(rvtInfo.Elements, state);
        diag?.WriteLine($"Own computed wall thickness: occurrences={wallThicknessOccurrences}/{state.BasicWallTypeThicknessFeet.Count}");
        rvtInfo.SerializedDocumentGuid = state.DocumentDetachGuid;
        rvtInfo.SerializedFormatSettings = Compact.CompactFormatSettings.Create(
            state.OwnDocumentUnitOptions, state.OwnDocumentUnitConversions, state.OwnDocumentSchemas);
        var lengthOptions = state.OwnDocumentLengthUnitOptions(out string? lengthUnitIssue);
        rvtInfo.SerializedLengthUnit = state.WideLengthUnit
            ?? (lengthOptions == null ? null : Ifc.IfcLengthUnitChoice.StoredLengthUnit(lengthOptions.Value, out lengthUnitIssue));
        if (rvtInfo.SerializedLengthUnit == null)
            rvtInfo.SerializedLengthUnitIssue = state.WideLengthUnitIssue ?? lengthUnitIssue;
        // BasicFileInfo "Worksharing: Not enabled | Local | Central"; any other text is not decided.
        rvtInfo.SerializedIsWorkshared = fileMetadata.GetValueOrDefault("Worksharing") switch
        {
            "Not enabled" => false,
            "Local" or "Central" => true,
            _ => null,
        };
        var (exportIds, replacedUniqueIds) = ElementBuilder.ApplyExportIds(rvtInfo.Elements, state);
        diag?.WriteLine($"Element export ids: episodes={state.HistoryEpisodeGuids.Count}/{state.EpisodeTableX?.ToString() ?? "?"}, "
            + $"records={state.ElemRecHistories?.Count.ToString() ?? "unavailable"}, elements={exportIds}, "
            + $"replaced UniqueIds={replacedUniqueIds}, document GUID={state.DocumentDetachGuid?.ToString() ?? "unavailable"}");
        diag?.WriteLine($"Wall height overrides: {ElementBuilder.WallHeightOverrides}, skipped-no-elev: {ElementBuilder.WallHeightSkippedNoElev}; level-elev candidates: {state.LevelElevCandidates.Count}");

        LogPhase("Export ids applied");
        ResolveParameterUnits(rvtInfo, state);

        // Shared-coordinates / true-north placement: the project may be modelled
        // axis-aligned but exported (by Revit) rotated+offset into shared coords. The exact
        // source is the active project location; the survey-point / rotation-vote search
        // stays only as the fallback for files whose active location cannot be read.
        var projectLocation = ResolveActiveProjectLocation(state, diag);
        rvtInfo.SharedTransform = projectLocation?.SharedTransform ?? FindSharedTransform(
            state.Chunks, state.BasePointCoordinates, state.Records, diag);
        if (projectLocation != null)
        {
            rvtInfo.SharedTransformIsActiveLocation = true;
            rvtInfo.SiteLatitudeRadians = projectLocation.Latitude;
            rvtInfo.SiteLongitudeRadians = projectLocation.Longitude;
            rvtInfo.SiteElevationFeet = projectLocation.ElevationFeet;
            rvtInfo.SitePlaceName = projectLocation.PlaceName;
        }
        if (rvtInfo.SharedTransform is { } stf)
            diag?.WriteLine($"Shared transform:angle={Math.Atan2(stf[1], stf[0]) * 180 / Math.PI:F4}deg origin=({stf[9]:F2},{stf[10]:F2},{stf[11]:F2})");

        LogPhase("Done");
        diag?.WriteLine($"Output: {rvtInfo.Elements.Count} elements, " +
            $"{rvtInfo.Elements.Count(e => e.Parameters.Count > 0)} with params, " +
            $"{rvtInfo.Elements.Count(e => e.Geometry != null)} with geometry");
        int sketchOwners = rvtInfo.Elements.Count(e => e.SketchLoops != null);
        if (sketchOwners > 0)
            diag?.WriteLine($"Sketch topology: {sketchOwners} owners, " +
                $"{rvtInfo.Elements.Where(e => e.SketchLoops != null).Sum(e => e.SketchLoops!.Count)} loops");

        rvtInfo.UserParameters = UserParameterCatalog.Build(state);
        LastParsingState = state;
        return rvtInfo;
    }

    internal static void MergeElemScanStringsInChunkOrder(
        IReadOnlyList<Dictionary<uint, List<string>>?> partialResults,
        Dictionary<uint, List<string>> elemScanStrings)
    {
        // HashSet-backed dedupe: rebar-heavy models attribute thousands of strings per
        // element, and List.Contains per insert made this merge quadratic.
        var mergeSeen = new Dictionary<uint, HashSet<string>>();
        for (int chunkIndex = 0; chunkIndex < partialResults.Count; chunkIndex++)
        {
            var partial = partialResults[chunkIndex];
            if (partial == null) continue;

            foreach (var (eid, strings) in partial)
            {
                if (!elemScanStrings.TryGetValue(eid, out var list))
                    elemScanStrings[eid] = list = new List<string>();
                if (!mergeSeen.TryGetValue(eid, out var seen))
                    mergeSeen[eid] = seen = new HashSet<string>(list);
                foreach (var s in strings)
                    if (seen.Add(s)) list.Add(s);
            }
        }
    }

    /// <summary>Read and parse a Revit file, returning both the structured data and internal parsing state.</summary>
    internal (RevitFileInfo Info, ParsingState State) ReadWithState(string rvtPath)
    {
        var diag = _options.DiagnosticOutput;
        using var diagnosticScope = DiagnosticSink.Use(diag);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        void LogPhase(string name) { diag?.WriteLine($"[{sw.Elapsed.TotalSeconds:F1}s] {name}"); }

        using var root = RootStorage.OpenRead(rvtPath);

        var elemTable = ElemTableReader.Read(root, out var elemWorksets, out _, out bool elemTableIsExact,
            ElemTableRecover.Limits(_options.RaiseLimits), _options.Diagnostics);
        var fileMetadata = BasicFileInfoReader.Read(root);
        var projectInfo = ProjectInfoReader.Read(root);
        var transmissionData = TransmissionDataReader.Read(root);
        var (previewBase64, previewMime) = PreviewImageReader.Read(root);

        LogPhase("Decompressing partitions...");
        var chunks = DecompressPartitions(root, diag, out var ownPartitionWallParts, out var partitionStreams, out var framedStreams, out bool staleCopiesRemoved);

        // Episode GUID table: nested stream Global/History (see Read() above).
        var (elemRecHistories, elemRecOwners, wideSchema) = ReadElemRecs(root);
        var episodeTable = ReadEpisodeTable(root, diag, wideSchema);
        bool r22View = false;
        IReadOnlyList<PartitionFraming.Framed?> readFrames = framedStreams;
        int partitionChunkCount = partitionStreams.Sum(s => s.Count);
        if (wideSchema != null && _options.R22Layout
            && RewriteToR22Layout(root, wideSchema, chunks, partitionStreams, framedStreams, diag) is { } r22)
        {
            (chunks, partitionChunkCount, var r22Frames) = r22;
            readFrames = r22Frames;
            r22View = true;
        }

        var state = new ParsingState
        {
            ElemTable = elemTable,
            ElemWorksets = elemWorksets,
            AllKnownIds = new HashSet<uint>(elemTable.Keys),
            Chunks = chunks,
            OwnPartitionWallParts = ownPartitionWallParts,
            LocalUsername = fileMetadata.GetValueOrDefault("Username"),
            EpisodeGuids = episodeTable.Guids,
            EpisodeTableX = episodeTable.X,
            HistoryEpisodeGuids = episodeTable.Guids,
            ElemRecHistories = elemRecHistories,
            ElemRecOwningElementIds = elemRecOwners,
            R22View = r22View,
            StaleCopiesRemoved = staleCopiesRemoved,
            FramedPartitions = readFrames,
            PartitionChunkCount = partitionChunkCount,
        };
        ReadWideElementIndex(root, state, partitionStreams, framedStreams, wideSchema, diag);
        ReadRebarBars(root, state, partitionStreams, framedStreams, wideSchema);
        ClassifyAndParseRecords(chunks, state, diag);
        ParseBlocks(state, diag);
        RunTfStreamWalk(state);
        if (state.WideSchema == null) ReadOwnDocumentUnits(root, state, diag);
        diag?.WriteLine($"Grid datums: {state.GridDatums.Count}");

        var rvtInfo = new RevitFileInfo
        {
            ElemTableIsExact = elemTableIsExact,
            GeometryUndecoded = state.WideSchema != null,
            WideLayoutNotRewritten = state.WideSchema != null && !state.R22View,
            Metadata = new FileMetadata
            {
                StreamVersion = fileMetadata.GetValueOrDefault("StreamVersion"),
                RevitVersion = fileMetadata.GetValueOrDefault("RevitVersion"),
                Build = fileMetadata.GetValueOrDefault("Build"),
                LastSavePath = fileMetadata.GetValueOrDefault("LastSavePath"),
                SavedLocale = fileMetadata.GetValueOrDefault("Locale when saved"),
                PreviewImage = previewBase64,
                PreviewImageMimeType = previewMime,
            },
            ProjectInfo = new ProjectInfo
            {
                Name = projectInfo.GetValueOrDefault("Name"),
                Number = projectInfo.GetValueOrDefault("Number"),
                Client = projectInfo.GetValueOrDefault("Client"),
                Address = projectInfo.GetValueOrDefault("Address"),
                Status = projectInfo.GetValueOrDefault("Status"),
                IssueDate = projectInfo.GetValueOrDefault("IssueDate"),
                Title = projectInfo.GetValueOrDefault("Title"),
                RevitVersion = projectInfo.GetValueOrDefault("RevitVersion"),
                Author = projectInfo.GetValueOrDefault("Author"),
                BuildingName = projectInfo.GetValueOrDefault("BuildingName"),
                OrganizationName = projectInfo.GetValueOrDefault("OrganizationName"),
            },
            TransmissionData = transmissionData,
        };

        return (rvtInfo, state);
    }

    /// <summary>Diagnostics: the release saved in BasicFileInfo, the release of the file's own schema and the one that decides the layout.</summary>
    private static void LogRelease(RootStorage root, Dictionary<string, string> fileMetadata, FileSchema? wideSchema, TextWriter? diag)
    {
        if (diag == null) return;
        int? schemaRelease = wideSchema != null ? SchemaRelease.FromSchema(wideSchema)
            : FormatsSchema.TryRead(root) is { } schema ? SchemaRelease.FromSchema(schema) : null;
        diag.WriteLine(SchemaRelease.Describe(SchemaRelease.Saved(fileMetadata.GetValueOrDefault("RevitVersion")), schemaRelease));
    }

    /// <summary>Element table records of the file: the Revit 2022 layout (shipped schema tables) or, when that does not
    /// apply, the 64-bit layout declared by the file's own schema, in which case the schema is returned too.</summary>
    private static (Dictionary<uint, ElemRecHistory>? Histories, Dictionary<uint, int>? Owners, FileSchema? WideSchema)
        ReadElemRecs(RootStorage root)
    {
        if (PagedGzipStream.Read(root, "Global", "ElemTable") is not { } bytes) return (null, null, null);
        var schema = FileSchema.Read(root);
        // the layout shipped for Revit 2022 applies to a file whose release in force (SchemaRelease.Of) is 2022 or unknown
        if (SchemaRelease.AllowsShipped(schema?.Release ?? SchemaRelease.Of(root, null), 2022)
            && ElementExportIds.ParseElemTable(bytes, out var owners) is { } histories) return (histories, owners, null);
        if (schema != null && WideElementRecords.ParseElemTable(bytes, schema) is { } wide)
            return (wide.Histories, wide.OwningElementIds, schema);
        return (null, null, null);
    }

    /// <summary>The bars of the reinforcement elements and the beam systems (<see cref="RebarReader"/>), by the file's own class schema in every layout.</summary>
    private static void ReadRebarBars(RootStorage root, ParsingState state,
        IReadOnlyList<IReadOnlyList<byte[]>> partitionStreams, IReadOnlyList<PartitionFraming.Framed?> framedStreams, FileSchema? wideSchema)
    {
        if ((wideSchema ?? FileSchema.Read(root)) is not { } schema) return;
        var reinforcement = RebarReader.Read(partitionStreams, schema, state.AllKnownIds, EntitySchemaReader.Read(root, schema.Document), passUnknownEntities: true,
            framed: framedStreams);
        foreach (var (id, bars) in reinforcement.Bars) state.RebarBars[id] = bars;
        foreach (var (id, joist) in reinforcement.Joists) state.JoistSystems[id] = joist;
    }

    /// <summary>Header category and root class of every ElemTable element of a 64-bit-id file (no-op for the other layouts).</summary>
    private static void ReadWideElementIndex(RootStorage root, ParsingState state,
        IReadOnlyList<IReadOnlyList<byte[]>> partitionStreams, IReadOnlyList<PartitionFraming.Framed?> framedStreams, FileSchema? schema, TextWriter? diag)
    {
        if (schema == null) return;
        state.WideSchema = schema;
        // the field walk of the root blocks (schema by class and field name); a file whose schema table does not parse a second time has none
        WideNativeParams? fields = null;
        SchemaDocument? schemaDocument = null;
        if (PagedGzipStream.Read(root, "Formats", "Latest") is { } schemaBytes)
        {
            try
            {
                fields = new WideNativeParams(schemaDocument = TfSchemaReader.Parse(schemaBytes));
                // the values of an extensible storage entity are read by its schema, so the objects behind them are read in place
                fields.Walker.UseEntitySchemas(EntitySchemaReader.Read(root, schemaDocument), passUnknown: true);
            }
            catch (FormatException) { diag?.WriteLine("Wide element records: Formats/Latest does not parse for the field walk"); }
        }
        var walks = new Dictionary<uint, List<(WideBlockWalker.Walk Walk, int Class)>>();
        var drivers = new Dictionary<uint, List<CurveDriverData>>();
        var wallShim = new Dictionary<uint, ElementRecord>();
        var index = WideElementRecords.BuildIndex(partitionStreams, schema, state.AllKnownIds,
            fields == null ? null : (id, data, offset, size, cls) =>
            {
                if (!walks.TryGetValue(id, out var list)) walks[id] = list = [];
                list.Add((fields.Walker.Run(data, offset, size, cls), cls));
                // the roots of a straight or an arc wall: the block of the drive curve (the classes the Revit 2022 reader names)
                if (state.StaleCopiesRemoved && fields.R22Id(cls) is 3524 or 361)
                {
                    wallShim[id] = new ElementRecord { ElemId = id, CategoryId = -2000011 };
                    if (!drivers.TryGetValue(id, out var found)) drivers[id] = found = [];
                    BlockParser.ExtractCurveDriverFromBlock(data, offset, offset + size, id, drivers, wallShim);
                }
            }, framedStreams);
        if (index == null)
        {
            diag?.WriteLine("Wide element records: the file's schema does not declare the expected ElementHeader");
            return;
        }
        foreach (var (id, category) in index.HeaderCategories) state.HeaderCategories[id] = category;
        foreach (var (id, option) in index.HeaderDesignOptions) state.HeaderDesignOptions[id] = option;
        foreach (var (id, box) in index.HeaderBoundingBoxes) state.HeaderBoundingBoxes[id] = box;
        foreach (var (id, classId) in index.RootClasses) state.NativeRootClassNames[id] = schema.Get(classId)!.Name;
        int walked = 0, exact = 0;
        var walkFailures = new Dictionary<string, int>();
        if (fields != null)
            foreach (var (id, list) in walks)
            {
                // a copy of the element in two streams is decoded only when it is one record
                if (list.Count != 1 || !index.RootClasses.TryGetValue(id, out int rootClass) || list[0].Class != rootClass) continue;
                if (drivers.TryGetValue(id, out var exactDrivers) && exactDrivers.Count > 0) state.ExactCurveDrivers[id] = exactDrivers;
                var np = fields.Build(list[0].Walk, rootClass);
                if (np == null) continue;
                if (fields.LevelElevation(list[0].Walk, rootClass) is double levelElevation)
                {
                    np.Fields["__isLevel"] = 1;
                    np.DoubleFields["__levelElevation"] = levelElevation;
                }
                if (fields.OwnName(list[0].Walk, rootClass) is { } ownName) state.WideNames[id] = ownName;
                if (fields.ProjectPhaseOrder(list[0].Walk, rootClass) is { } phaseOrder)
                    state.ProjectPhaseOrderSource = (state.ProjectPhaseOrderSource.BlockCount + 1, phaseOrder);
                if (fields.RoomPhase(list[0].Walk, rootClass) is int roomPhase) state.ExactRoomPhaseIds[id] = roomPhase;
                walked++;
                if (list[0].Walk.Exact) exact++;
                else if (diag != null)
                {
                    var key = schema.Get(rootClass)!.Name + ": " + (list[0].Walk.Error ?? "no error, short of the payload end");
                    walkFailures[key] = walkFailures.GetValueOrDefault(key) + 1;
                }
                state.NativeParams[id] = np;
                state.NativeParamsFromRootClass.Add(id);
                if (fields.R22Id(rootClass) is > 0 and <= ushort.MaxValue and int r22)
                {
                    state.ElemClassIdx[id] = (ushort)r22;
                    // the element's own root (its m_id is the id): the class number of the Revit 2022 table, by class name
                    if (np.Fields.TryGetValue("m_id", out int ownId) && ownId == (int)id)
                        state.OwnedElementClassRoots[id] = new(1, (ushort)r22);
                    // the source the Revit 2022 reader records for the system families: the symbol roots (with their discriminator)
                    // and the field of an occurrence that names its type
                    if (TfBlockDeserializer.IsSubclassOf(r22, 106))
                        // a duct type is told apart by the class of its profile (AbsCurveType.m_Profile), as the Revit 2022 reader does
                        state.SystemTypeRoots[id] = (1, (ushort)r22, r22 is 110 or 114
                            ? np.Fields.TryGetValue("104:m_Profile", out int profileClass) && profileClass > 0 ? profileClass : null
                            : fields.SystemDiscriminator(list[0].Walk, rootClass));
                    if (TfBlockDeserializer.SystemOccurrenceTypeField((ushort)r22) is var (declaring, typeField)
                        && np.Fields.TryGetValue(declaring == 602 ? "602:m_idType" : typeField, out int occurrenceType)
                        && occurrenceType > 0 && occurrenceType != (int)id)
                    {
                        state.SystemOccurrenceTypeIds[id] = (1, (ushort)r22, occurrenceType);
                        state.SchemaSystemOccurrenceOwners.Add(id);
                    }
                }
            }
        diag?.WriteLine($"Wide element records: field walk of {walked} root blocks, {exact} consumed exactly");
        foreach (var (reason, count) in walkFailures.OrderByDescending(f => f.Value).Take(15))
            diag?.WriteLine($"  walk stopped x{count}: {reason}");
        if (fields != null && schemaDocument != null) ReadWideDocumentUnits(root, state, schemaDocument, fields, walks, diag);
        diag?.WriteLine($"Wide element records: {index.Records} headers, {index.Blocks} blocks, "
            + $"categories={index.HeaderCategories.Count}, root classes={index.RootClasses.Count} of {state.AllKnownIds.Count} elements");
    }

    /// <summary>The document units of a file read by the schema walk: the units element named by Global/Latest, its FormatOptions and the
    /// persisted schema maps (see <see cref="WideDocument"/>), recorded where the Revit 2022 reader records the same facts. A file of the
    /// Revit 2020 layout has no schema maps; its Length option is the first display unit of the units element.</summary>
    private static void ReadWideDocumentUnits(RootStorage root, ParsingState state, SchemaDocument schemaDocument, WideNativeParams fields,
        Dictionary<uint, List<(WideBlockWalker.Walk Walk, int Class)>> walks, TextWriter? diag)
    {
        void Fail(string reason)
        {
            state.WideLengthUnitIssue = reason;
            diag?.WriteLine($"Wide document units: {reason}");
        }
        string? failure = "Global/Latest is missing or rejected by CRC/length decoding";
        if (PagedGzipStream.Read(root, "Global", "Latest") is not { } latest
            || WideDocument.Read(latest, schemaDocument, out failure) is not { } document)
        {
            Fail($"Global/Latest has no readable units element and schema maps ({failure})");
            return;
        }
        state.OwnDocumentUnitsElemId = document.UnitsElemId;
        if (document.Units != null && document.Parameters != null)
            RecordOwnDocumentSchemas(state, new TfBlockDeserializer.DocumentSchemaPrefix(0, 0, 0, document.Units, document.Parameters, [], [], []), diag);
        if (!walks.TryGetValue(document.UnitsElemId, out var blocks) || blocks.Count != 1 || !state.NativeParams.TryGetValue(document.UnitsElemId, out var np))
        {
            Fail($"no unique decoded UnitsElem block for id {document.UnitsElemId} (blocks {blocks?.Count ?? 0})");
            return;
        }
        var records = blocks[0].Walk.Records.Select(r => (fields.Walker.Class(r.ClassId)!.Name, r.Values)).ToList();
        if (!np.Fields.TryGetValue("m_id", out int owner) || (uint)owner != document.UnitsElemId
            || !np.Fields.TryGetValue("m_famId", out int family) || !np.Fields.TryGetValue("m_docAccess", out int access))
        {
            Fail($"the UnitsElem block {document.UnitsElemId} has no matching owner fields");
            return;
        }
        if (document.Units == null)
        {
            string? legacyIssue = "the UnitsElem is not document accessible";
            if (access == 1) state.WideLengthUnit = WideDocument.LegacyLengthUnit(records, out legacyIssue);
            if (state.WideLengthUnit == null) Fail(legacyIssue!);
            else diag?.WriteLine($"Wide document units: UnitsElem {document.UnitsElemId}, display unit layout, length {state.WideLengthUnit}");
            return;
        }
        if (WideDocument.UnitOptions(records) is not { } options)
        {
            Fail($"the UnitsElem block {document.UnitsElemId} does not have the format option shape");
            return;
        }
        // kept apart from OwnUnitOptions, which the continuous walk revokes for these files (it counts the block a second time)
        string? issue = "the UnitsElem is not document accessible";
        if (access == 1) state.WideLengthUnit = Ifc.IfcLengthUnitChoice.StoredLengthUnit(options, out issue);
        if (state.WideLengthUnit == null) state.WideLengthUnitIssue = issue;
        state.OwnUnitOptions[document.UnitsElemId] = (1, new TfBlockDeserializer.UnitsElemOptions(document.UnitsElemId, family, access, 0, 0, options));
        diag?.WriteLine($"Wide document units: UnitsElem {document.UnitsElemId}, {options.Options.Length} format options, unit schemas {document.Units.Entries.Length}");
    }

    private List<byte[]> DecompressPartitions(RootStorage root, TextWriter? diag,
        out Dictionary<uint, OwnPartitionParts.WallParts>? ownPartitionWallParts,
        out List<IReadOnlyList<byte[]>> partitionStreams, out List<PartitionFraming.Framed?> framedStreams, out bool staleCopiesRemoved)
    {
        staleCopiesRemoved = false;
        var partStor = root.OpenStorage("Partitions");
        var partEntries = partStor.EnumerateEntries()
            .Where(e => e.Type == EntryType.Stream)
            .OrderByDescending(e => e.Length)
            .ToList();
        if (partEntries.Count == 0) throw new InvalidOperationException("No stream found in Partitions storage");

        var decompressor = new ChunkDecompressor();
        var allChunks = new List<byte[]>();
        partitionStreams = new List<IReadOnlyList<byte[]>>();
        framedStreams = [];
        ownPartitionWallParts = null;
        var routing = _options.ResolveCurrentCopies && partEntries.Count > 1 ? PlanRecordRouting(root, partStor, partEntries.Select(e => e.Name).ToList(), diag) : null;
        // the exact framing of the partition streams (PartitionFraming), when the class table names its marker classes and the id width is known
        var framing = FormatsSchema.TryRead(root) is { } formats && PartitionFraming.TagsOf(formats) is { } framingTags
            && SchemaRelease.IdWidth(SchemaRelease.Effective(SchemaRelease.Saved(BasicFileInfoReader.Read(root).GetValueOrDefault("RevitVersion")),
                SchemaRelease.FromSchema(formats))) is int framingIdWidth ? (Tags: framingTags, IdWidth: framingIdWidth) : ((PartitionFraming.Tags Tags, int IdWidth)?)null;

        foreach (var partEntry in partEntries)
        {
            diag?.WriteLine($"Partitions stream: '{partEntry.Name}' ({partEntry.Length:N0} bytes)");
            using var partStream = partStor.OpenStream(partEntry.Name);
            var raw = new byte[partStream.Length];
            partStream.ReadExactly(raw);

            // shortcut: saved format9 with one partition stream; multi-stream scope needs its own load validation.
            if (partEntries.Count == 1 && partEntry.Name == "0")
            {
                var framingSchema = FormatsSchema.TryRead(root);
                var wallClasses = framingSchema == null ? null : OwnPartitionParts.ClassesOf(framingSchema);
                string? failure;
                ownPartitionWallParts = wallClasses != null && ElemTableLayout.IdWidth(framingSchema!) is int framingWidth
                    ? OwnPartitionParts.Read(raw, wallClasses, framingWidth, out failure)
                    : Fail("the schema lacks the framing or wall classes", out failure);
                diag?.WriteLine($"  Own partition wall parts: {ownPartitionWallParts?.Count.ToString() ?? "unavailable"} ({failure ?? "exact"})");
            }

            var chunks = CutPartition(raw, framing, decompressor, out var stats, out string? framingFailure, out var framed);
            RecordStream("Partitions/" + partEntry.Name, raw.Length, stats.Members, stats.Decoded, stats.Failed, stats.Recovered, stats.Unverified);
            diag?.WriteLine(framingFailure == null ? $"  Chunks: {chunks.Count} segments of the exact framing"
                : $"  Chunks: {chunks.Count} decompressed by a search for gzip members (framing: {framingFailure})");
            if (routing != null && int.TryParse(partEntry.Name, out int partitionNumber)
                && RecordRouting.Stale(routing.Census, routing.Current, partitionNumber) is { Count: > 0 } stale)
            {
                if (RecordRouting.Prune(chunks, routing.Census.SegmentLengths[partitionNumber], stale) is { } pruned)
                {
                    chunks = pruned;
                    staleCopiesRemoved = true;
                    diag?.WriteLine($"  Record routing: {stale.Count} records of {partEntry.Name} are not the current copy and were removed");
                }
                else diag?.WriteLine($"  Record routing: the chunks of {partEntry.Name} are not the framed segments; {stale.Count} stale records kept");
                // the records of the framing leave out the copies that are not current (both walks give the same offsets)
                if (framed != null) RecordRouting.Drop(framed.Items, stale);
            }
            allChunks.AddRange(chunks);
            partitionStreams.Add(chunks);
            framedStreams.Add(framed);
        }

        diag?.WriteLine($"Total chunks: {allChunks.Count} decompressed");
        return allChunks;
    }

    private static Dictionary<uint, OwnPartitionParts.WallParts>? Fail(string reason, out string? failure) { failure = reason; return null; }

    /// <summary>The chunks of a partition stream: when its framing holds (<see cref="PartitionFraming"/>), the output of each segment, every one a
    /// gzip member checked by its CRC and length, and <paramref name="framingFailure"/> null; otherwise the members a search for gzip headers
    /// finds, with the recovery of damaged ones (<see cref="ChunkDecompressor"/>), and the reason the framing does not hold.</summary>
    internal static List<byte[]> CutPartition(byte[] raw, (PartitionFraming.Tags Tags, int IdWidth)? framing, ChunkDecompressor decompressor,
        out (int Members, int Decoded, int Failed, int Recovered, int Unverified) stats, out string? framingFailure, out PartitionFraming.Framed? framed)
    {
        var segments = new List<byte[]>();
        var items = new List<PartitionFraming.ProjectItem>();
        framed = null;
        framingFailure = "the class table names no framing classes or the id width is not known";
        if (framing is var (tags, idWidth) && PartitionFraming.Walk(raw, tags, idWidth, r =>
            {
                if (r.Block == 0 && r.Owner != (idWidth == 8 ? ulong.MaxValue : uint.MaxValue))
                    items.Add(new(r.Channel, r.Owner, r.Offset, r.Item.Length));
            }, out framingFailure, out _, null, segments))
        {
            stats = (segments.Count, segments.Count, 0, 0, 0);
            framed = new PartitionFraming.Framed(segments, items, idWidth);
            return segments;
        }
        var chunks = decompressor.DecompressAll(raw);
        stats = decompressor.LastStats;
        return chunks;
    }

    private sealed record RoutingPlan(RecordRouting.Census Census, Dictionary<ulong, int> Current);

    /// <summary>The current copy of every element that several partition streams hold (<see cref="RecordRouting"/>), or null with the
    /// reason in the diagnostics when the framing, the element table or the increment table does not decode.</summary>
    private static RoutingPlan? PlanRecordRouting(RootStorage root, Storage partStor, IReadOnlyList<string> names, TextWriter? diag)
    {
        var schema = FormatsSchema.TryRead(root);
        var census = RecordRouting.Begin(schema, out var failure);
        if (census == null) { diag?.WriteLine($"Record routing: not applied ({failure})"); return null; }
        if (PagedGzipStream.Read(root, "Global", "ElemTable") is not { } elemTable || ElemTableLayout.TryParse(elemTable, schema!) is not { } rows)
        { diag?.WriteLine("Record routing: not applied (ElemTable does not decode by the schema)"); return null; }
        if (PagedGzipStream.Read(root, "Global", "DocumentIncrementTable") is not { } incrementBytes
            || DocumentIncrementTable.TryParse(incrementBytes, schema!, out failure) is not { } table)
        { diag?.WriteLine($"Record routing: not applied (DocumentIncrementTable: {failure ?? "missing"})"); return null; }
        foreach (string name in names)
        {
            if (!int.TryParse(name, out int partition)) { diag?.WriteLine($"Record routing: not applied (partition stream '{name}')"); return null; }
            using var stream = partStor.OpenStream(name);
            var raw = new byte[stream.Length];
            stream.ReadExactly(raw);
            if (!RecordRouting.Collect(census, partition, raw, out failure))
            { diag?.WriteLine($"Record routing: not applied (framing of partition {name}: {failure})"); return null; }
        }
        var current = RecordRouting.Current(census, rows, table);
        diag?.WriteLine($"Record routing: {current.Count} elements are held by several partitions; {census.KeyedRecords} keyed-block records are not project elements");
        return new RoutingPlan(census, current);
    }

    private void RecordStream(string name, long rawBytes, int members, int decoded, int failed, int recovered, int unverified)
    {
        if (_options.Diagnostics == null) return;
        if (_options.Diagnostics.Streams.Any(s => s.Name == name)) return;
        _options.Diagnostics.Streams.Add(new StreamRecord(name, rawBytes, members, decoded, failed, recovered, unverified));
    }

    private List<byte[]> DecompressStorage(RootStorage root, string storageName, TextWriter? diag)
    {
        try
        {
            var stor = root.OpenStorage(storageName);
            var decompressor = new ChunkDecompressor();
            var allChunks = new List<byte[]>();
            foreach (var entry in stor.EnumerateEntries().Where(e => e.Type == EntryType.Stream))
            {
                // Skip ElemTable (already parsed separately) and small index streams
                if (entry.Name == "ElemTable" || entry.Length < 100) continue;
                using var stream = stor.OpenStream(entry.Name);
                var raw = new byte[stream.Length];
                stream.ReadExactly(raw);
                try
                {
                    // Global/Formats page framing must be removed before CRC-checked inflation.
                    if (PagedGzipStream.Decompress(raw) is { } exact)
                    {
                        diag?.WriteLine($"{storageName}/{entry.Name}: exact paged decode ({exact.Length:N0} bytes)");
                        RecordStream($"{storageName}/{entry.Name}", raw.Length, 1, 1, 0, 0, 0);
                        allChunks.Add(exact);
                        continue;
                    }
                    var chunks = decompressor.DecompressAll(raw);
                    var (members, decodedMembers, failedMembers, recoveredMembers, unverifiedMembers) = decompressor.LastStats;
                    RecordStream($"{storageName}/{entry.Name}", raw.Length, members, decodedMembers, failedMembers, recoveredMembers, unverifiedMembers);
                    if (chunks.Count > 0)
                    {
                        diag?.WriteLine($"{storageName}/{entry.Name}: {chunks.Count} chunks ({raw.Length:N0} bytes)");
                        allChunks.AddRange(chunks);
                    }
                }
                catch (Exception ex) when (MalformedInput.Is(ex))
                {
                    diag?.WriteLine($"{storageName}/{entry.Name}: skipped, not decodable ({ex.GetType().Name}: {ex.Message})");
                    RecordStream($"{storageName}/{entry.Name}", raw.Length, 1, 0, 1, 0, 0);
                }
            }
            return allChunks;
        }
        catch (Exception ex) when (MalformedInput.Is(ex))
        {
            diag?.WriteLine($"{storageName}: unreadable ({ex.GetType().Name}: {ex.Message})");
            return [];
        }
    }

    /// <summary>The episode list of Global/History. The exact paged decode
    /// (<see cref="PagedGzipStream"/>) is used when it succeeds; a stream longer than one page
    /// otherwise inflates past a page segment into wrong records. The chunk decoder remains the
    /// fallback for streams the exact decode rejects.</summary>
    private EpisodeTableParseResult ReadEpisodeTable(RootStorage root, TextWriter? diag, FileSchema? wideSchema = null)
    {
        if (PagedGzipStream.Read(root, "Global", "History", out long rawLength) is { } exact)
        {
            diag?.WriteLine($"Global/History: exact paged decode ({exact.Length:N0} bytes)");
            RecordStream("Global/History", rawLength, 1, 1, 0, 0, 0);
            return ParseEpisodeGuids(new List<byte[]> { exact }, diag, wideSchema);
        }
        return ParseEpisodeGuids(DecompressNamedStream(root, "Global", "History", diag), diag, wideSchema);
    }

    /// <summary>Decompress a single named stream nested inside a storage (e.g. the "History"
    /// stream inside the "Global" storage). Unlike <see cref="DecompressStorage"/> this targets
    /// one specific stream rather than enumerating everything a storage holds. Mirrors
    /// <see cref="DecompressStorage"/>'s degrade-to-empty behavior: a missing storage/stream or
    /// a decompression failure never throws, it just yields an empty list.</summary>
    private List<byte[]> DecompressNamedStream(RootStorage root, string storageName, string streamName, TextWriter? diag)
    {
        try
        {
            var stor = root.OpenStorage(storageName);
            using var stream = stor.OpenStream(streamName);
            var raw = new byte[stream.Length];
            stream.ReadExactly(raw);
            var decompressor = new ChunkDecompressor();
            var chunks = decompressor.DecompressAll(raw);
            var (members, decodedMembers, failedMembers, recoveredMembers, unverifiedMembers) = decompressor.LastStats;
            RecordStream($"{storageName}/{streamName}", raw.Length, members, decodedMembers, failedMembers, recoveredMembers, unverifiedMembers);
            diag?.WriteLine($"{storageName}/{streamName}: {chunks.Count} chunks ({raw.Length:N0} bytes)");
            return chunks;
        }
        catch (Exception ex) when (MalformedInput.Is(ex))
        {
            diag?.WriteLine($"{storageName}/{streamName}: unreadable ({ex.GetType().Name}: {ex.Message})");
            RecordStream($"{storageName}/{streamName}", 0, 1, 0, 1, 0, 0);
            return [];
        }
    }

    // Global/History episode GUID table layout. The stream opens with a fixed 102-byte header (8-byte signature + 6 bytes +
    // two u32 fields at offsets 14/18 + five 16-byte GUID slots at 22/38/54/70/86), followed by a variable-length "extra" u32
    // list whose element count sits at offset 102, followed by a single u32 (X) immediately before the record array, then
    // back-to-back fixed-stride records (16-byte GUID + 1-byte tag) with no per-record padding:
    //
    //   base = 110 + 4 * u32@102        (110 = 102-byte header + 4-byte extra-count + 4-byte X)
    //   X    = u32@(base - 4)
    //   records start at base, stride 17
    //
    // GUID byte order is little-endian, i.e. C# new Guid(byte[]) applied directly to the 16 raw bytes.
    /// <summary>Byte offset of the u32 giving the length, in u32 units, of the "extra" list that
    /// follows the fixed 102-byte header. The record-array base is <c>110 + 4 * u32@102</c>.</summary>
    private const int EpisodeTableExtraCountOffset = 102;
    /// <summary>Fixed distance from the stream start to the record array when the "extra" list is
    /// empty: the 102-byte fixed header, plus the 4-byte extra-count field itself, plus the
    /// 4-byte <c>X</c> field that immediately precedes the record array.</summary>
    private const int EpisodeTableBaseFixedLength = 110;

    /// <summary>The five GUID slots of the fixed header at 22/38/54/70/86 hold DocumentHistory's
    /// m_creationGUID, m_detachGUID, m_upgradeGUID, m_previousUpgradeGUID and m_saveAsGUID, in
    /// schema order. The detach GUID is the document GUID of the export ids: on detached models only
    /// m_detachGUID reproduces the project, site, building and storey GlobalIds.</summary>
    internal const int EpisodeTableDetachGuidOffset = 38;
    private const int DocumentHistoryClassId = 1186;

    private static Guid? ReadDetachGuid(byte[] chunk)
    {
        var fields = TfBlockDeserializer.SerializationFields(DocumentHistoryClassId);
        if (fields == null
            || !fields.Select(field => field.Name).Where(name => name.EndsWith("GUID", StringComparison.Ordinal))
                .SequenceEqual(new[] { "m_creationGUID", "m_detachGUID", "m_upgradeGUID", "m_previousUpgradeGUID", "m_saveAsGUID" })
            || chunk.Length < EpisodeTableDetachGuidOffset + 16)
            return null;
        var guid = new Guid(chunk.AsSpan(EpisodeTableDetachGuidOffset, 16));
        return guid == Guid.Empty ? null : guid;
    }
    /// <summary>Record size: 16-byte GUID + 1-byte tag.</summary>
    private const int EpisodeTableStride = 17;
    /// <summary>Literal header signature that opens a genuine episode GUID table. Anything at Global/History that
    /// doesn't start with these exact bytes is not the table (corrupt data, an unrelated stream, or a future format
    /// reusing the same path) and must not be parsed as one.</summary>
    private static readonly byte[] EpisodeTableHeaderSignature = { 0xA2, 0x04, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00 };

    /// <summary>Result of <see cref="ParseEpisodeGuids"/>: the ordered episode GUID table plus
    /// <c>X</c>, the u32 read from the 4 bytes immediately preceding the record array (null when
    /// no valid table was found). <c>X</c> reads as the document's lifetime episode counter;
    /// callers that need <c>N = X - 1</c> (e.g. <c>index = N - creationDate</c>) read it from here.
    /// This type only carries the value.</summary>
    internal sealed class EpisodeTableParseResult
    {
        public List<string> Guids { get; init; } = new();
        public uint? X { get; init; }
        /// <summary>DocumentHistory.m_detachGUID, the second GUID slot of the fixed header
        /// (<see cref="EpisodeTableDetachGuidOffset"/>); null without a valid table, when the
        /// schema does not declare the slot order, or when the slot is empty.</summary>
        public Guid? DetachGuid { get; init; }
    }

    /// <summary>
    /// Parses the document's episode GUID table from the decompressed Global/History stream:
    /// slot index -&gt; GUID, plus <c>X</c> (see <see cref="EpisodeTableParseResult"/>). A missing stream, a stream whose first
    /// bytes don't match <see cref="EpisodeTableHeaderSignature"/> (this is what confirms the bytes are the table), a stream
    /// too short to hold the u32 extra-count field, a computed record-array base beyond the end of the stream, or a base
    /// that leaves no whole record, all degrade cleanly to an empty table with a null <c>X</c>; this never throws.
    /// The trailing bytes after the last full record are intentionally not validated: the tail size differs between files
    /// whose tables parse cleanly under the same base/stride.
    /// Internal (not private) so unit tests can feed synthetic byte buffers directly.
    /// </summary>
    internal static EpisodeTableParseResult ParseEpisodeGuids(IReadOnlyList<byte[]> historyChunks, TextWriter? diag,
        FileSchema? wideSchema = null)
    {
        // The table opens with the DocumentHistory class id (a number of the file's own schema) and the constant 1.
        byte[] signature = EpisodeTableHeaderSignature;
        if (wideSchema != null)
        {
            if (wideSchema.ByName("DocumentHistory") is not { } documentHistory) return new EpisodeTableParseResult();
            signature = [.. BitConverter.GetBytes((ushort)documentHistory.Id), .. EpisodeTableHeaderSignature[2..]];
        }
        var result = new List<string>();
        uint? x = null;
        Guid? detachGuid = null;
        int usedBase = -1;
        bool sawHeaderMismatch = false;
        bool sawBaseOutOfRange = false;
        var tagCounts = new Dictionary<byte, int>();
        foreach (var chunk in historyChunks)
        {
            if (chunk.Length < signature.Length
                || !chunk.AsSpan(0, signature.Length).SequenceEqual(signature))
            {
                sawHeaderMismatch = true;
                continue;
            }

            // Need the 4 bytes at EpisodeTableExtraCountOffset to compute the record-array base.
            if (chunk.Length < EpisodeTableExtraCountOffset + 4)
            {
                sawBaseOutOfRange = true;
                continue;
            }

            uint extraCount = BitConverter.ToUInt32(chunk, EpisodeTableExtraCountOffset);
            // extraCount comes straight from file bytes (corrupt input is possible); widen to
            // long before multiplying so a huge value can't wrap a 32-bit computation into
            // something that looks in-range.
            long baseLong = EpisodeTableBaseFixedLength + 4L * extraCount;
            if (baseLong > chunk.Length)
            {
                sawBaseOutOfRange = true;
                continue;
            }
            int baseOffset = (int)baseLong;

            var chunkResult = new List<string>();
            var chunkTagCounts = new Dictionary<byte, int>();
            for (int off = baseOffset; off + 16 <= chunk.Length; off += EpisodeTableStride)
            {
                var raw = new byte[16];
                Array.Copy(chunk, off, raw, 0, 16);
                chunkResult.Add(new Guid(raw).ToString("D"));

                // Tag byte, when present: reported as a diagnostic statistic only; it is not constant,
                // so it cannot be used to reject individual records.
                int tagOff = off + 16;
                if (tagOff < chunk.Length)
                {
                    byte tag = chunk[tagOff];
                    chunkTagCounts[tag] = chunkTagCounts.GetValueOrDefault(tag) + 1;
                }
            }
            if (chunkResult.Count > 0)
            {
                // X sits in the 4 bytes immediately before the record array; only trust it once
                // we know the base actually led to real records (chunkResult.Count > 0) rather
                // than landing exactly at the end of the stream.
                result = chunkResult;
                tagCounts = chunkTagCounts;
                x = BitConverter.ToUInt32(chunk, baseOffset - 4);
                detachGuid = wideSchema == null ? ReadDetachGuid(chunk) : null; // the slot order is validated for the 2022 schema only
                usedBase = baseOffset;
                break;
            }
            else
            {
                // Header + extra-count matched but the computed base left no whole record (e.g.
                // it landed exactly at chunk.Length): still degrade cleanly.
                sawBaseOutOfRange = true;
            }
        }

        if (result.Count > 0)
        {
            string tagStats = string.Join(", ", tagCounts.OrderByDescending(kv => kv.Value)
                .Select(kv => $"0x{kv.Key:X2}={kv.Value}"));
            diag?.WriteLine($"Episode GUIDs: {result.Count} (base={usedBase}, X={x}, tags: {tagStats})");
        }
        else if (sawHeaderMismatch)
        {
            diag?.WriteLine("Episode GUIDs: 0 (header signature mismatch, not the episode table)");
        }
        else if (sawBaseOutOfRange)
        {
            diag?.WriteLine("Episode GUIDs: 0 (computed record-array base out of range, not a valid episode table)");
        }
        else
        {
            diag?.WriteLine("Episode GUIDs: 0");
        }
        return new EpisodeTableParseResult { Guids = result, X = x, DetachGuid = detachGuid };
    }

    private static void ClassifyAndParseRecords(List<byte[]> chunks, ParsingState state, TextWriter? diag)
    {
        foreach (var chunk in chunks)
        {
            bool hasRecords = RecordParser.ContainsAnchoredR22Record(
                chunk, state.AllKnownIds);
            bool isR25 = !hasRecords && RecordParser.ContainsAnchoredR25Record(
                chunk, state.AllKnownIds);
            hasRecords |= isR25;
            if (hasRecords)
            {
                state.RecordChunks.Add(chunk);
                if (isR25)
                    RecordParser.ParseChunkR25(chunk, state);
                else
                    RecordParser.ParseChunk(chunk, state);
            }
            else
            {
                state.NonRecordChunks.Add(chunk);
            }
        }

        var extraCount = state.Records.Keys.Count(k => !state.ElemTable.ContainsKey(k));
        diag?.WriteLine($"Records: {state.Records.Count} ({state.Records.Count - extraCount} in ElemTable + {extraCount} discovered)");

    }

    /// <summary>Records exact terminal-name provenance only for the three type-side
    /// classes. A second owned block makes the source ambiguous and permanently clears its
    /// value; instance-side and every other class are ignored.</summary>
    internal static void RecordTypeNameSource(
        ParsingState state, uint fullClassIdx, uint elementId, string? terminalName)
    {
        Dictionary<uint, (int BlockCount, string? TerminalName)>? source =
            fullClassIdx switch
            {
                0xFFFF0200 => state.CompoundStructureTypeNameSource,
                0xFFFF077B => state.FloorTypeNameSource,
                0xFFFF0286 => state.CeilingTypeNameSource,
                _ => null,
            };
        if (source == null)
            return;

        if (source.TryGetValue(elementId, out var existingSource))
        {
            source[elementId] = (existingSource.BlockCount + 1, null);
        }
        else
        {
            source[elementId] = (1, terminalName);
        }
    }

    internal static void RecordCurtainFamilyHost(
        ParsingState state, byte[] data, int start, uint size, ushort classId, uint owner)
    {
        if (!TfBlockDeserializer.IsSubclassOf(classId, 1794)) return;
        var host = start >= 0 && start <= data.Length - 4
            && BitConverter.ToUInt32(data, start) == owner
                ? TfBlockDeserializer.ExtractCurtainFamilyHost(data, start, size, classId) : null;
        state.CurtainFamilyHostSource[owner] = state.CurtainFamilyHostSource.TryGetValue(owner, out var old)
            ? (old.BlockCount + 1, null) : (1, host);
    }

    internal static void RecordBasicWallTypeFunction(
        ParsingState state, byte[] data, int start, uint size, ushort classId, uint owner)
    {
        if (!TfBlockDeserializer.IsSubclassOf(classId, 513)) return;
        int? value = start >= 0 && start <= data.Length - 4
            && BitConverter.ToUInt32(data, start) == owner
            && TfBlockDeserializer.TryExtractBasicWallTypeFunction(data, start, size, classId, out int function)
                ? function : null;
        state.BasicWallTypeFunctions[owner] = state.BasicWallTypeFunctions.TryGetValue(owner, out var old)
            ? (old.BlockCount + 1, null) : (1, value);
    }

    internal static void RecordStackedWallTypeRoot(
        ParsingState state, byte[] data, int start, uint size, ushort classId, uint owner)
    {
        if (start >= 0 && start <= data.Length - 4
            && BitConverter.ToUInt32(data, start) == owner
            && TfBlockDeserializer.IsExactStackedWallTypeRoot(data, start, size, classId))
            state.StackedWallTypeOwners.Add(owner);
    }

    /// <summary>Records a curtain-wall function only when the complete owning block is
    /// validated to be an exact NewCurtainWallType root with a matching serialized owner id.</summary>
    internal static void RecordNewCurtainWallTypeFunction(
        ParsingState state, byte[] segment, int blockStart, uint dataSize,
        ushort classId, uint elementId)
    {
        if (!TfBlockDeserializer.IsSubclassOf(classId, 513)) return;
        int? value = blockStart >= 0
            && blockStart <= segment.Length - 4
            && BitConverter.ToUInt32(segment, blockStart) == elementId
            && TfBlockDeserializer.TryExtractNewCurtainWallTypeFunction(
                segment, blockStart, dataSize, classId, out int function)
                    ? function : null;
        state.NewCurtainWallTypeFunctions[elementId] =
            state.NewCurtainWallTypeFunctions.TryGetValue(elementId, out var old)
                ? (old.BlockCount + 1, null) : (1, value);
    }

    internal static void RecordNewCurtainWallTypeName(
        ParsingState state, byte[] data, int start, uint size, ushort classId, uint owner)
    {
        if (start >= 0 && start <= data.Length - 4 && BitConverter.ToUInt32(data, start) == owner
            && TfBlockDeserializer.ExtractNewCurtainWallTypeName(data, start, size, classId) is string name)
            state.NewCurtainWallTypeNames[owner] = name;
    }

    internal static void RecordGroupMembers(
        ParsingState state, byte[] data, int start, uint size, ushort classId, uint owner)
    {
        if (classId == 1322 && start >= 0 && start <= data.Length - 4 && BitConverter.ToUInt32(data, start) == owner
            && TfBlockDeserializer.ExtractGroupMembers(data, start, size, classId) is { } members)
            state.GroupMembers[owner] = members;
    }

    internal static void RecordBasicWallTypeName(
        ParsingState state, byte[] data, int start, uint size, ushort classId, uint owner)
    {
        if (start >= 0 && start <= data.Length - 4 && BitConverter.ToUInt32(data, start) == owner
            && TfBlockDeserializer.ExtractBasicWallTypeName(data, start, size, classId) is string name)
            state.BasicWallTypeNames[owner] = name;
    }

    internal static void RecordWallSweepReferenceSource(
        ParsingState state, byte[] data, int start, uint size, ushort classId, uint owner)
    {
        bool ownerMatches = start >= 0 && start <= data.Length - 4 && BitConverter.ToUInt32(data, start) == owner;
        if (TfBlockDeserializer.IsSubclassOf(classId, 363))
        {
            int? typeId = ownerMatches ? TfBlockDeserializer.ExtractWallSweepTypeId(data, start, size, classId) : null;
            state.WallSweepTypeIds[owner] = state.WallSweepTypeIds.TryGetValue(owner, out var old)
                ? (old.BlockCount + 1, null) : (1, typeId);
        }
        if (TfBlockDeserializer.IsSubclassOf(classId, 106))
        {
            string? name = ownerMatches ? TfBlockDeserializer.ExtractCorniceTypeName(data, start, size, classId) : null;
            state.CorniceTypeNames[owner] = state.CorniceTypeNames.TryGetValue(owner, out var old)
                ? (old.BlockCount + 1, null) : (1, name);
        }
    }

    internal static void RecordFamilySymbolReferenceSource(
        ParsingState state, byte[] data, int start, uint size, ushort classId, uint owner)
    {
        RecordOwnFamilyCutoutInputs(state, data, start, size, classId, owner);
        bool ownerMatches = start >= 0 && start <= data.Length - 4 && BitConverter.ToUInt32(data, start) == owner;
        if (TfBlockDeserializer.IsSubclassOf(classId, 1794))
        {
            int? typeId = ownerMatches ? TfBlockDeserializer.ExtractFamilyInstanceSymbolId(data, start, size, classId) : null;
            var computed = ownerMatches ? TfBlockDeserializer.ExtractComputedFamilySymbol(data, start, size, classId) : null;
            // A root that reads the same class, type, symbol, placement and host as the one before confirms nothing different (a file
            // saved in several increments keeps the old copy of an instance whose other fields changed), so it does not make the
            // instance ambiguous. A root that differs in any of them still does.
            if (RepeatsInstanceRoot(state, owner, classId, typeId, computed)) return;
            state.FamilyInstanceSymbolIds[owner] = state.FamilyInstanceSymbolIds.TryGetValue(owner, out var old)
                ? (old.BlockCount + 1, null) : (1, typeId);
            state.ComputedFamilySymbols[owner] = state.ComputedFamilySymbols.TryGetValue(owner, out var prior)
                ? (prior.BlockCount + 1, null) : (1, computed);
        }
        if (TfBlockDeserializer.IsSubclassOf(classId, 106))
        {
            string? name = ownerMatches ? TfBlockDeserializer.ExtractFamilySymbolName(data, start, size, classId) : null;
            state.FamilySymbolNames[owner] = state.FamilySymbolNames.TryGetValue(owner, out var old)
                ? (old.BlockCount + 1, null) : (1, name);
        }
        if (TfBlockDeserializer.IsSubclassOf(classId, 37))
        {
            ushort? kind = ownerMatches
                && TfBlockDeserializer.TryExtractOwnElementClass(data, start, size, classId, out int ownId)
                && ownId == owner ? classId : null;
            state.ComputedSymbolTargetClasses[owner] = state.ComputedSymbolTargetClasses.TryGetValue(owner, out var prior)
                ? (prior.BlockCount + 1, null) : (1, kind);
        }
    }

    internal static bool RepeatsInstanceRoot(ParsingState state, uint owner, ushort classId, int? typeId,
        TfBlockDeserializer.ComputedFamilySymbol? computed)
    {
        if (typeId is null || computed is null
            || !state.FamilyInstanceSymbolIds.TryGetValue(owner, out var ids) || ids.BlockCount != 1 || ids.TypeId != typeId
            || !state.ComputedFamilySymbols.TryGetValue(owner, out var symbols) || symbols.BlockCount != 1 || symbols.Symbol is not { } first
            || !state.ComputedSymbolTargetClasses.TryGetValue(owner, out var kind) || kind.BlockCount != 1 || kind.ClassId != classId)
            return false;
        return first.SymbolId == computed.SymbolId && first.HostId == computed.HostId
            && first.SubInstanceCount == computed.SubInstanceCount && first.InsertOrientation == computed.InsertOrientation
            && (first.Transform is null ? computed.Transform is null
                : computed.Transform is not null && first.Transform.AsSpan().SequenceEqual(computed.Transform));
    }

    internal static void RecordSystemTypeRoot(
        ParsingState state, byte[] data, int start, uint size, ushort classId, uint owner)
    {
        if (!TfBlockDeserializer.IsSubclassOf(classId, 106)) return;
        int? discriminator = null;
        bool exact = start >= 0 && start <= data.Length - 4 && BitConverter.ToUInt32(data, start) == owner
            && TfBlockDeserializer.TryExtractSystemTypeRoot(data, start, size, classId, out discriminator);
        // A pipe, flex pipe, pipe insulation or cable tray type can occur twice in the stream (two root blocks).
        // A repeat that reads the same class, discriminator and name confirms nothing different.
        if (exact && classId is 3227 or 3181 or 3217 or 3130 or 3135
            && TfBlockDeserializer.ExtractMepTypeName(data, start, size, classId) is { } repeatedName
            && state.SystemTypeRoots.TryGetValue(owner, out var sameRoot) && sameRoot == (1, classId, discriminator)
            && state.MepTypeNames.TryGetValue(owner, out var sameName) && sameName == (1, classId, repeatedName))
            return;
        // the field walk of a 64-bit-id file records the root of a type once; the same block seen again as its transcoded copy would count it twice
        if (state.WideSchema == null || !state.SystemTypeRoots.ContainsKey(owner))
            state.SystemTypeRoots[owner] = state.SystemTypeRoots.TryGetValue(owner, out var old)
                ? (old.BlockCount + 1, (ushort)0, null)
                : (1, exact ? classId : (ushort)0, exact ? discriminator : null);
        if (classId is 3718 or 3697 or 3736)
        {
            string? name = exact ? TfBlockDeserializer.ExtractStairComponentTypeName(data, start, size, classId) : null;
            state.StairComponentTypeNames[owner] = state.StairComponentTypeNames.TryGetValue(owner, out var previous)
                ? (previous.BlockCount + 1, (ushort)0, null) : (1, classId, name);
        }
        if (classId == 110)
        {
            string? name = exact ? TfBlockDeserializer.ExtractDuctTypeName(data, start, size, classId) : null;
            state.DuctTypeNames[owner] = state.DuctTypeNames.TryGetValue(owner, out var previous)
                ? (previous.BlockCount + 1, null) : (1, name);
        }
        if (classId is 3227 or 3181 or 3217 or 3130 or 3135)
        {
            string? name = exact ? TfBlockDeserializer.ExtractMepTypeName(data, start, size, classId) : null;
            state.MepTypeNames[owner] = state.MepTypeNames.TryGetValue(owner, out var previous)
                ? (previous.BlockCount + 1, (ushort)0, null) : (1, classId, name);
        }
    }

    internal static void RecordSystemOccurrenceTypeId(
        ParsingState state, byte[] data, int start, uint size, ushort classId, uint owner)
    {
        if (!TfBlockDeserializer.IsSubclassOf(classId, 363)) return;
        bool ownerMatches = start >= 0 && start <= data.Length - 4 && BitConverter.ToUInt32(data, start) == owner;
        int? typeId = ownerMatches ? TfBlockDeserializer.ExtractSystemOccurrenceTypeId(data, start, size, classId) : null;
        // A repeated block of an MEP curve that names the same type of the same class confirms nothing different.
        if (typeId is > 0 && TfBlockDeserializer.IsSubclassOf(classId, 602)
            && state.SystemOccurrenceTypeIds.TryGetValue(owner, out var same) && same == (1, classId, typeId))
            return;
        // A schema alone cannot resolve duplicate legacy roots.
        if (state.SchemaSystemOccurrenceOwners.Contains(owner)) return;
        state.SystemOccurrenceTypeIds[owner] = state.SystemOccurrenceTypeIds.TryGetValue(owner, out var old)
            ? (old.BlockCount + 1, (ushort)0, null) : (1, classId, typeId);
    }

    internal static void RecordMaterialName(
        ParsingState state, byte[] data, int start, uint size, ushort classId, uint owner)
    {
        if (classId != 2486) return;
        string? name = start >= 0 && start <= data.Length - 4
            && BitConverter.ToUInt32(data, start) == owner
                ? TfBlockDeserializer.ExtractMaterialName(data, start, size, classId)
                : null;
        state.MaterialNames[owner] = state.MaterialNames.TryGetValue(owner, out var old)
            ? (old.BlockCount + 1, null)
            : (1, name);
    }

    internal static void RecordGraphicsStyle(
        ParsingState state, byte[] data, int start, uint size, ushort classId, uint owner)
    {
        if (classId != 2050) return;
        int categoryId = 0, materialId = 0;
        bool read = start >= 0 && start <= data.Length - 4
            && BitConverter.ToUInt32(data, start) == owner
            && TfBlockDeserializer.TryExtractGraphicsStyle(data, start, size, classId, out categoryId, out materialId);
        state.GraphicsStyles[owner] = state.GraphicsStyles.TryGetValue(owner, out var old)
            ? (old.BlockCount + 1, null, 0)
            : (1, read ? materialId : null, categoryId);
    }

    internal static void RecordCategoryName(
        ParsingState state, byte[] data, int start, uint size, ushort classId, uint owner)
    {
        if (classId != 632) return;
        string? name = start >= 0 && start <= data.Length - 4
            && BitConverter.ToUInt32(data, start) == owner
                ? TfBlockDeserializer.ExtractCategoryName(data, start, size, classId)
                : null;
        state.CategoryNames[owner] = state.CategoryNames.TryGetValue(owner, out var old)
            ? (old.BlockCount + 1, null)
            : (1, name);
    }

    internal static void RecordWallJoinedToTop(
        ParsingState state, byte[] data, int start, uint size, ushort classId, uint owner)
    {
        if (classId is not (3524 or 361)) return;
        bool? joinedToTop = start >= 0 && start <= data.Length - 4
            && BitConverter.ToUInt32(data, start) == owner
                ? TfBlockDeserializer.ExtractWallJoinedToTop(data, start, size, classId)
                : null;
        state.WallJoinedToTop[owner] = state.WallJoinedToTop.TryGetValue(owner, out var old)
            ? (old.BlockCount + 1, null)
            : (1, joinedToTop);
    }

    internal static void RecordWallEndJoins(
        ParsingState state, byte[] data, int start, uint size, ushort classId, uint owner)
    {
        if (classId is not (3524 or 361)) return;
        string? failure = "owner";
        var joins = start >= 0 && start <= data.Length - 4
            && BitConverter.ToUInt32(data, start) == owner
                ? TfBlockDeserializer.ExtractWallEndJoins(data, start, size, classId, out failure)
                : null;
        if (joins == null && failure != null)
            state.WallEndJoinFailures[failure] = state.WallEndJoinFailures.GetValueOrDefault(failure) + 1;
        state.WallEndJoins[owner] = state.WallEndJoins.TryGetValue(owner, out var old)
            ? (old.BlockCount + 1, null)
            : (1, joins);
    }

    internal static void RecordRoofOrRailingReferenceSource(
        ParsingState state, byte[] data, int start, uint size, ushort classId, uint owner)
    {
        bool ownerMatches = start >= 0 && start <= data.Length - 4 && BitConverter.ToUInt32(data, start) == owner;
        if (TfBlockDeserializer.IsSubclassOf(classId, 363) || TfBlockDeserializer.IsSubclassOf(classId, 485))
        {
            int? typeId = ownerMatches ? TfBlockDeserializer.ExtractRoofOrRailingTypeId(data, start, size, classId) : null;
            state.RoofOrRailingTypeIds[owner] = state.RoofOrRailingTypeIds.TryGetValue(owner, out var old)
                ? (old.BlockCount + 1, classId, null) : (1, classId, typeId);
        }
        if (TfBlockDeserializer.IsSubclassOf(classId, 106))
        {
            string? name = ownerMatches ? TfBlockDeserializer.ExtractRoofOrRailingTypeName(data, start, size, classId) : null;
            state.RoofOrRailingTypeNames[owner] = state.RoofOrRailingTypeNames.TryGetValue(owner, out var old)
                ? (old.BlockCount + 1, classId, null) : (1, classId, name);
        }
    }

    /// <summary>Records exact named CompoundStructure layers only for the owning
    /// 0xFFFF0286 CompoundCeilingType root. Every matching root contributes to the
    /// count; a malformed first root or any duplicate permanently clears Layers.</summary>
    internal static void RecordCeilingCompoundStructureLayerSource(
        ParsingState state, byte[] segment, int blockStart, uint dataSize,
        ushort classId, uint elementId)
    {
        if (classId != 646
            || blockStart < 0
            || blockStart > segment.Length - 16
            || BitConverter.ToUInt32(segment, blockStart) != elementId
            || BitConverter.ToUInt32(segment, blockStart + 12) != 0xFFFF0286)
            return;

        RawCompoundStructureLayer[]? layers =
            TfBlockDeserializer.TryExtractCompoundCeilingLayers(
                segment, blockStart, dataSize, classId, out var decoded)
                ? decoded
                : null;
        if (state.CeilingCompoundStructureLayerSource.TryGetValue(
            elementId, out var existing))
        {
            state.CeilingCompoundStructureLayerSource[elementId] =
                (existing.BlockCount + 1, null);
        }
        else
        {
            state.CeilingCompoundStructureLayerSource[elementId] = (1, layers);
        }
    }

    private static void ReadOwnDocumentUnits(RootStorage root, ParsingState state, TextWriter? diag)
    {
        byte[]? data = PagedGzipStream.Read(root, "Global", "Latest");
        if (data == null)
        {
            state.OwnDocumentUnitsElemId = null;
            RecordOwnDocumentSchemas(state, null, diag);
            diag?.WriteLine("Own document units unavailable: Global/Latest missing or rejected by CRC/length decoding");
            return;
        }
        var link = TfBlockDeserializer.ExtractOwnDocumentUnitsLink(data, out string? failure);
        state.OwnDocumentUnitsElemId = link?.UnitsElemId;
        diag?.WriteLine(link == null ? $"Own document units unavailable: {failure}"
            : $"Own document UnitsElem: {link.UnitsElemId}; unique complete document root: {state.OwnDocumentUnitOptions != null}");
        var schemas = TfBlockDeserializer.ExtractOwnDocumentSchemas(data, out string? schemaFailure);
        RecordOwnDocumentSchemas(state, schemas, diag);
        diag?.WriteLine(schemas == null ? $"Own document schemas unavailable: {schemaFailure}"
            : $"Own document schemas: units={schemas.Units.Entries.Length}, parameters={schemas.Parameters.Entries.Length}");
    }

    internal static void RecordOwnDocumentSchemas(ParsingState state,
        TfBlockDeserializer.DocumentSchemaPrefix? schemas, TextWriter? diag)
    {
        state.OwnDocumentSchemas = schemas == null ? null : (schemas.Units, schemas.Parameters);
        state.OwnDocumentSiFactors = null;
        state.OwnDocumentUnitCores = null;
        state.OwnDocumentUnitConversions = null;
        if (schemas == null) return;
        state.OwnDocumentSiFactors = UnitSchemaEvaluator.EvaluateOwn(schemas.Units, out string? failure);
        diag?.WriteLine(state.OwnDocumentSiFactors == null ? $"Own document SI projection unavailable: {failure}"
            : $"Own document SI factors: {state.OwnDocumentSiFactors.Count}; not public/internal-unit conversion");
        if (state.OwnDocumentSiFactors == null) return;
        state.OwnDocumentUnitCores = UnitSchemaEvaluator.EvaluateCore(schemas.Units, out failure);
        diag?.WriteLine(state.OwnDocumentUnitCores == null ? $"Own document unit core unavailable: {failure}"
            : $"Own document unit cores: {state.OwnDocumentUnitCores.Count}; not display/public conversion");
        if (state.OwnDocumentUnitCores == null) return;
        state.OwnDocumentUnitConversions = UnitSchemaEvaluator.EvaluateDisplay(schemas.Units, out failure);
        diag?.WriteLine(state.OwnDocumentUnitConversions == null ? $"Own document numeric display unavailable: {failure}"
            : $"Own document numeric display: normal={state.OwnDocumentUnitConversions.NormalByName.Count}, legacy={state.OwnDocumentUnitConversions.LegacyExact.Count}");
    }

    internal static void RecordOwnUnitOptions(
        ParsingState state, byte[] segment, int start, uint size, ushort classId, uint owner)
    {
        if (!TfBlockDeserializer.IsSubclassOf(classId, 4029)) return;
        var units = start >= 0 && start <= segment.Length - 16
            && BitConverter.ToUInt32(segment, start) == owner
            ? TfBlockDeserializer.ExtractOwnUnitOptions(segment, start, size, classId) : null;
        state.OwnUnitOptions[owner] = state.OwnUnitOptions.TryGetValue(owner, out var existing)
            ? (existing.BlockCount + 1, null) : (1, units);
    }

    internal static void RecordHostTypeCompoundStructure(
        ParsingState state, byte[] segment, int start, uint size, ushort classId, uint owner)
    {
        if (!TfBlockDeserializer.IsSubclassOf(classId, 105)) return;
        RawCompoundStructureLayer[]? layers = null;
        string? name = null;
        if (start >= 0 && start <= segment.Length - 16
            && BitConverter.ToUInt32(segment, start) == owner
            && TfBlockDeserializer.TryExtractHostTypeLayers(segment, start, size, classId, out var decoded))
        {
            layers = decoded;
            name = TfBlockDeserializer.ExtractHostTypeOwnName(segment, start, size, classId);
        }
        state.HostTypeCompoundStructures[owner] = state.HostTypeCompoundStructures.TryGetValue(owner, out var old)
            ? (old.BlockCount + 1, 0, null, null) : (1, classId, layers, name);
        if (classId == 646 || state.CompoundCeilingTypeThicknessFeet.ContainsKey(owner))
            state.CompoundCeilingTypeThicknessFeet[owner] = classId == 646
                && state.HostTypeCompoundStructures[owner].BlockCount == 1
                && start >= 0 && start <= segment.Length - 16
                && BitConverter.ToUInt32(segment, start) == owner
                && TfBlockDeserializer.TryExtractCompoundCeilingTypeThicknessFeet(
                    segment, start, size, classId, out double thicknessFeet)
                    ? thicknessFeet : null;
        if (classId == 512 || state.BasicWallTypeThicknessFeet.ContainsKey(owner))
            state.BasicWallTypeThicknessFeet[owner] = classId == 512
                && state.HostTypeCompoundStructures[owner].BlockCount == 1
                && start >= 0 && start <= segment.Length - 16
                && BitConverter.ToUInt32(segment, start) == owner
                && TfBlockDeserializer.TryExtractBasicWallTypeThicknessFeet(
                    segment, start, size, classId, out double wallThicknessFeet)
                    ? wallThicknessFeet : null;
    }

    /// <summary>Records exact StairsElement/StairsType schema-root source. Every matching
    /// low-16 class block is counted; invalid framing/root data and every duplicate clear the
    /// retained root so selection can never depend on stream order.</summary>
    internal static void RecordStairRootSource(
        ParsingState state, byte[] segment, int blockStart, uint dataSize,
        ushort classId, uint elementId)
    {
        Dictionary<uint, ExactTfRootSource>? source = classId switch
        {
            3681 => state.StairElementRootSource,
            3742 => state.StairTypeRootSource,
            _ => null,
        };
        if (source == null)
            return;

        TfBlockDeserializer.NativeParams? root = null;
        bool ownerHeaderMatches = blockStart >= 0
            && blockStart <= segment.Length - 4
            && BitConverter.ToUInt32(segment, blockStart) == elementId;
        if (ownerHeaderMatches)
        {
            bool valid = classId == 3681
                ? TfBlockDeserializer.TryExtractExactStairElementRoot(
                    segment, blockStart, dataSize, classId, out root)
                : TfBlockDeserializer.TryExtractExactStairsTypeRoot(
                    segment, blockStart, dataSize, classId, out root);
            if (!valid) root = null;
        }

        if (source.TryGetValue(elementId, out ExactTfRootSource? existing))
            source[elementId] = new(existing.BlockCount + 1, null);
        else
            source[elementId] = new(1, root);
    }

    /// <summary>Retain exact room names only for a single valid owner block.</summary>
    internal static void RecordRoomNameSource(
        ParsingState state, byte[] segment, int start, uint size, ushort classId, uint owner)
    {
        if (classId != 3471) return;
        string? name = null;
        int? phase = null;
        bool? locationless = null;
        int? circuit = null;
        if (start >= 0 && start <= segment.Length - 4
            && BitConverter.ToUInt32(segment, start) == owner
            && TfBlockDeserializer.TryExtractExactRoomMetadata(segment, start, size, classId, out var value, out phase, out locationless, out circuit))
            name = value;
        state.RoomNameSource[owner] = state.RoomNameSource.TryGetValue(owner, out var existing)
            ? (existing.BlockCount + 1, null) : (1, name);
        state.ExactRoomPhaseIds.Remove(owner);
        state.ExactRoomLocationless.Remove(owner);
        state.ExactRoomCircuitIds.Remove(owner);
        if (state.RoomNameSource[owner].BlockCount == 1 && circuit is { } cachedCircuit)
            state.ExactRoomCircuitIds[owner] = cachedCircuit;
        // A project phase can have id 0 when the file's layout is read from its own schema; the
        // Revit 2022 layout numbers phases from 1.
        if (state.RoomNameSource[owner].BlockCount == 1 && name != null
            && (phase is > 0 || state.WideSchema != null && phase is >= 0))
            state.ExactRoomPhaseIds[owner] = phase.Value;
        if (state.RoomNameSource[owner].BlockCount == 1 && name != null && locationless is { } flag)
            state.ExactRoomLocationless[owner] = flag;
    }

    /// <summary>Retain the topology of a level room plan only for a single valid owner block.</summary>
    internal static void RecordRoomPlanSource(
        ParsingState state, byte[] segment, int start, uint size, ushort classId, uint owner)
    {
        if (classId != 2306) return;
        TfBlockDeserializer.RoomPlanTopology? plan = null;
        if (start >= 0 && start <= segment.Length - 4
            && BitConverter.ToUInt32(segment, start) == owner
            && TfBlockDeserializer.TryExtractExactRoomPlan(segment, start, size, classId, out var value))
            plan = value;
        state.RoomPlanSource[owner] = state.RoomPlanSource.TryGetValue(owner, out var existing)
            ? (existing.BlockCount + 1, null) : (1, plan);
    }

    /// <summary>Keep the project-location records of the block stream (ActiveGeoLocationTrackingElement,
    /// GeoLocation, GeoSite). An element can be stored more than once as the file is saved; the later block is the
    /// current one (the same order the base points use), so each record replaces its element's earlier one.</summary>
    internal static void RecordProjectLocation(
        ParsingState state, byte[] segment, int start, uint size, ushort classId, uint owner)
    {
        if (classId is not (131 or 2070 or 2071)
            || start < 0 || start > segment.Length - 4
            || BitConverter.ToUInt32(segment, start) != owner) return;
        switch (classId)
        {
            case 131:
                if (TfBlockDeserializer.ExtractParams(segment, start, size, classId) is { } tracking
                    && tracking.Fields.TryGetValue("m_activeGeoLocationId", out int activeId) && activeId > 0)
                    state.ActiveGeoLocations[owner] = (uint)activeId;
                break;
            case 2070:
                if (TfBlockDeserializer.ExtractGeoLocation(segment, start, size, classId) is { } location)
                    state.GeoLocations[owner] = location;
                break;
            default:
                if (TfBlockDeserializer.ExtractParams(segment, start, size, classId) is { } site
                    && site.DoubleFields.TryGetValue("m_dLatitude", out double latitude)
                    && site.DoubleFields.TryGetValue("m_dLongitude", out double longitude))
                {
                    state.GeoSites[owner] = (latitude, longitude);
                    if (site.StringFields.TryGetValue("m_placeName", out string? placeName))
                        state.GeoSitePlaceNames[owner] = placeName;
                    else state.GeoSitePlaceNames.Remove(owner);
                }
                break;
        }
    }

    internal static void RecordProjectPhaseOrderSource(
        ParsingState state, byte[] segment, int start, uint size, ushort classId, uint owner)
    {
        // the field walk of a 64-bit-id file records the phase order once; this record of the transcoded block would count it twice
        if (classId != 187 || state.WideSchema != null) return;
        int[]? phases = null;
        if (start >= 0 && start <= segment.Length - 4
            && BitConverter.ToUInt32(segment, start) == owner)
            TfBlockDeserializer.TryExtractExactProjectPhaseOrder(segment, start, size, classId, out phases);
        var prior = state.ProjectPhaseOrderSource;
        state.ProjectPhaseOrderSource = (prior.BlockCount + 1, prior.BlockCount == 0 ? phases : null);
    }

    internal static void RecordSpaceBoundarySource(
        ParsingState state, byte[] segment, int start, uint size, ushort classId, uint owner)
    {
        if (classId is not (2007 or 2006)) return;
        if (state.SpaceBoundarySource.TryGetValue(owner, out var existing))
        {
            state.SpaceBoundarySource[owner] = (existing.BlockCount + 1, null);
            state.SpaceLiteralBoundaries.Remove(owner);
        }
        else
        {
            state.SpaceBoundarySource[owner] = (1, classId == 2007
                ? GRepParserV2.ParseExactPlanarSpaceBoundary(segment, start, size, owner)
                    ?? GRepParserV2.ParseCurvedSpaceBoundary(segment, start, size, owner) : null);
            if (classId == 2007 && GRepParserV2.ParseLiteralPlanarSpaceBoundary(segment, start, size, owner) is { } literal)
                state.SpaceLiteralBoundaries[owner] = literal;
        }
    }

    internal static void RecordWallLocationLine(
        ParsingState state, byte[] segment, int start, uint size, ushort classId, uint owner)
    {
        if (!TfBlockDeserializer.IsSubclassOf(classId, 37)) return;
        if (!state.OwnedElementClassRoots.TryGetValue(owner, out var root)
            || root.BlockCount != 1 || root.ClassId != classId)
        { state.OwnWallLocationLines.Remove(owner); return; }
        if (classId is not (3524 or 361)) return;
        var line = (!state.CurveDriverCandidates.TryGetValue(owner, out var candidates) || candidates.Count == 0)
            && start >= 0 && start <= segment.Length - 4 && BitConverter.ToUInt32(segment, start) == owner
            ? TfBlockDeserializer.ExtractOwnWallLocationLine(segment, start, size, classId, out _) : null;
        if (line != null) state.OwnWallLocationLines[owner] = line;
    }

    /// <summary>Records the panel order of a wall's curtain grid (<see cref="TfBlockDeserializer.ExtractCurtainGridPanelIds"/>).
    /// Every wall root counts: a second root of the same id makes the order ambiguous.</summary>
    internal static void RecordCurtainPanelOrder(
        ParsingState state, byte[] segment, int start, uint size, ushort classId, uint owner)
    {
        if (!TfBlockDeserializer.IsSubclassOf(classId, 362)) return;
        var ids = start >= 0 && start <= segment.Length - 4 && BitConverter.ToUInt32(segment, start) == owner
            ? TfBlockDeserializer.ExtractCurtainGridPanelIds(segment, start, size, classId) : null;
        state.CurtainPanelOrders[owner] = state.CurtainPanelOrders.TryGetValue(owner, out var old)
            ? (old.BlockCount + 1, null) : (1, ids);
    }

    /// <summary>The drive curve of a family instance in a file with its own schema: the field walk of the root block names no pointer
    /// target, so its GLine (CurveDriver.m_pCrv: origin, direction, end parameters) is read by the Revit 2022 tail walk over the
    /// rewritten block (the rewrite keeps the bytes of that object). Only the keys the field walk cannot give are added.</summary>
    internal static void AddDriveCurveFromRewrittenBlock(TfBlockDeserializer.NativeParams target, TfBlockDeserializer.NativeParams? tail)
    {
        if (tail == null) return;
        foreach (var (key, value) in tail.DoubleFields)
            if (key.StartsWith("87:m_pCrv.", StringComparison.Ordinal)) target.DoubleFields.TryAdd(key, value);
    }

    /// <summary>Hands <see cref="StairComponentSource.Record"/> the channel-102 records of the project's own block, one root object per
    /// element in its current copy, instead of every block the stream walk meets: the walk also meets the copies of an id in the documents
    /// of loaded families and byte runs inside other items, and each one counted as a second root of the element. The location line of a
    /// wall (<see cref="RecordWallLocationLine"/>) is read from its root record once every root is counted.</summary>
    internal static void RecordFramedRoots(ParsingState state)
    {
        var roots = new List<(byte[] Data, int Start, uint Size, ushort Class, uint Owner)>();
        foreach (var frame in state.FramedPartitions!)
            foreach (var record in frame!.Items)
            {
                if (record.Channel != 102 || record.Length < 18 || record.Owner > uint.MaxValue) continue;
                // the item [owner:4][checksum:4][size:4][class:2 ...][size:4] is the block layout the walk hands over
                var (data, start, _) = frame.Bytes(record);
                roots.Add((data, start, (uint)(record.Length - 16), BitConverter.ToUInt16(data, start + 12), (uint)record.Owner));
            }
        // a file with its own class schema has the roots of its element index seeded already (ReadWideElementIndex): the record replaces
        // the seed where it is counted; a root the count leaves out, such as one whose rewrite failed (unknown tag), keeps it
        if (state.WideSchema != null)
            foreach (var (_, _, _, classId, owner) in roots)
                if (StairComponentSource.CountsClassRoot(state, classId, owner)) state.OwnedElementClassRoots.Remove(owner);
        foreach (var (data, start, size, classId, owner) in roots) StairComponentSource.Record(state, data, start, size, classId, owner);
        foreach (var (data, start, size, classId, owner) in roots)
            if (classId is 3524 or 361) RecordWallLocationLine(state, data, start, size, classId, owner);
    }

    /// <summary>Hands <see cref="BlockParser.ParseBlock"/> the channel-102 and 103 records of the project's own block (the root objects and the
    /// geometry of the elements, in their current copy) instead of the blocks a scan of the partition chunks finds: the scan also takes the blocks
    /// of the documents of loaded families, whose ids repeat project ids, and it stops or jumps at a header-like byte run and at a block that runs
    /// into the next chunk.</summary>
    internal static void ParseFramedBlocks(ParsingState state, bool collectBlockOnlyIds, List<BlockParser.PendingGRep> pending)
    {
        foreach (var frame in state.FramedPartitions!)
            foreach (var record in frame!.Items)
            {
                if (record.Channel == 101 || record.Length < 18 || record.Owner is 0 or > uint.MaxValue) continue;
                var (data, start, copied) = frame.Bytes(record);
                uint eid = (uint)record.Owner;
                int queued = pending.Count;
                BlockParser.ParseBlock(data, start, eid, (uint)(record.Length - 16), state.AllKnownIds.Contains(eid), state, collectBlockOnlyIds, pending);
                // shortcut: the geometry of a record that runs into the next segment or is over the size limit of the scan is not decoded (the
                // scan never reached such a block, and the decoder takes minutes on some of them: 28 to 180 s each for ten 0.7 to 0.9 MB geometry
                // records of one model, none of which changed the output); decode it once the geometry decoder is linear in the block size.
                if ((copied || record.Length - 16 > BlockParser.MaxBlockSize) && pending.Count > queued)
                    pending.RemoveRange(queued, pending.Count - queued);
            }
    }
    /// <summary>Continuous-stream anchored block walk (see TfStreamWalker): native param
    /// stores + full-coverage re-run of block-driven signals (curve owners, node owners,
    /// registry categories, sketch/detail curves).</summary>
    private static void RunTfStreamWalk(ParsingState state)
    {
        // The walk reads 4-byte ids and Revit 2022 class numbers; a 64-bit-id file is decoded by WideElementRecords.
        if (state.WideSchema != null && !state.R22View) return;
        // Diagnostic: RVTIO_DUMP_GREP=<eid> dumps that element's raw G-blocks.
        string? dumpGrepEnv = Environment.GetEnvironmentVariable("RVTIO_DUMP_GREP");
        var dumpGrepEids = new HashSet<uint>();
        if (dumpGrepEnv != null)
            foreach (var part in dumpGrepEnv.Split(','))
                if (uint.TryParse(part, out uint dge)) dumpGrepEids.Add(dge);
        var walkGReps = new List<(byte[] Segment, int Start, uint Size, uint Eid)>();
        // the roots the class and stair sources count: the records of the project's block when every stream is read by its framing
        bool framedRoots = state.ReadsFramedRecords;
        if (framedRoots) RecordFramedRoots(state);
        TfStreamWalker.Walk(state.Chunks, state.AllKnownIds, (seg, bs, ds, cls, eid) =>
        {
            uint fullClassIdx = BitConverter.ToUInt32(seg, bs + 12);
            if (fullClassIdx is 0xFFFF0200 or 0xFFFF077B or 0xFFFF0286)
            {
                string? terminalName = BlockParser.ExtractTerminalElementName(
                    seg, bs + 16, bs + 16 + (int)ds);
                RecordTypeNameSource(state, fullClassIdx, eid, terminalName);
            }
            if (fullClassIdx == 0xFFFF0286)
                RecordCeilingCompoundStructureLayerSource(
                    state, seg, bs, (uint)ds, cls, eid);
            if (dumpGrepEids.Count > 0 && dumpGrepEids.Contains(eid))
            {
                try
                {
                    int len = (int)Math.Min(16L + ds, (long)seg.Length - bs);
                    var buf = new byte[len];
                    Array.Copy(seg, bs, buf, 0, len);
                    File.WriteAllBytes(Path.Combine(Path.GetTempPath(), $"grep_{eid}_{cls:X4}_{bs}.bin"), buf);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    DiagnosticSink.WriteLine($"Block dump failed: {ex.Message}");
                }
            }
            if (!state.ElemClassIdx.ContainsKey(eid)) state.ElemClassIdx[eid] = cls;
            if (!framedRoots) StairComponentSource.Record(state, seg, bs, (uint)ds, cls, eid);
            if (cls is 485 or 492)
            {
                var prior = state.RailingQuantityInputs.GetValueOrDefault(eid);
                state.RailingQuantityInputs[eid] = (prior.BlockCount + 1,
                    TfBlockDeserializer.ExtractOwnRailingQuantityInput(seg, bs, (uint)ds, cls));
            }
            if (!framedRoots) RecordWallLocationLine(state, seg, bs, (uint)ds, cls, eid);
            RecordCurtainPanelOrder(state, seg, bs, (uint)ds, cls, eid);
            if (TfBlockDeserializer.IsSubclassOf(cls, 1794))
                RecordCurtainFamilyHost(state, seg, bs, ds, cls, eid);
            if (cls is 3681 or 3742)
                RecordStairRootSource(state, seg, bs, (uint)ds, cls, eid);
            if (cls == 3471)
                RecordRoomNameSource(state, seg, bs, ds, cls, eid);
            if (cls == 2306)
                RecordRoomPlanSource(state, seg, bs, ds, cls, eid);
            if (cls == 187)
                RecordProjectPhaseOrderSource(state, seg, bs, ds, cls, eid);
            RecordProjectLocation(state, seg, bs, (uint)ds, cls, eid);
            RecordNewCurtainWallTypeFunction(state, seg, bs, (uint)ds, cls, eid);
            if (cls == 2559)
                RecordNewCurtainWallTypeName(state, seg, bs, ds, cls, eid);
            if (cls == 512)
                RecordBasicWallTypeName(state, seg, bs, ds, cls, eid);
            RecordGroupMembers(state, seg, bs, (uint)ds, cls, eid);
            RecordWallSweepReferenceSource(state, seg, bs, ds, cls, eid);
            RecordFamilySymbolReferenceSource(state, seg, bs, ds, cls, eid);
            RecordRoofOrRailingReferenceSource(state, seg, bs, ds, cls, eid);
            RecordSystemTypeRoot(state, seg, bs, ds, cls, eid);
            RecordHostTypeCompoundStructure(state, seg, bs, ds, cls, eid);
            RecordOwnUnitOptions(state, seg, bs, ds, cls, eid);
            RecordSystemOccurrenceTypeId(state, seg, bs, ds, cls, eid);
            RecordWallJoinedToTop(state, seg, bs, ds, cls, eid);
            RecordWallEndJoins(state, seg, bs, ds, cls, eid);
            RecordMaterialName(state, seg, bs, ds, cls, eid);
            RecordGraphicsStyle(state, seg, bs, ds, cls, eid);
            RecordCategoryName(state, seg, bs, ds, cls, eid);
            if (cls == 3666)
                RecordStackedWallTypeRoot(state, seg, bs, ds, cls, eid);
            if (TfBlockDeserializer.IsSubclassOf(cls, 513))
                RecordBasicWallTypeFunction(state, seg, bs, ds, cls, eid);
            // The element's own root block (its ElemTable class) wins over an earlier block of another class
            // (e.g. a FamilySymbol's geometry GRep decoded first).
            bool rootClassBlock = state.ElemClassIdx.TryGetValue(eid, out var rootClass) && rootClass == cls;
            if (!state.NativeParams.ContainsKey(eid) || cls is 2299 or 0x083C // Datum subclasses own their exact names/geometry
                || (rootClassBlock && !state.NativeParamsFromRootClass.Contains(eid)))
            {
                var np = TfBlockDeserializer.ExtractParams(seg, bs, (uint)ds, cls);
                if (rootClassBlock && np is { Count: > 0 }) state.NativeParamsFromRootClass.Add(eid);
                if (cls == 2299)
                {
                    np ??= new TfBlockDeserializer.NativeParams();
                    np.Fields["__isLevel"] = 1;
                    var levelName = TfBlockDeserializer.ExtractLevelName(
                        seg, bs, (uint)ds, cls);
                    if (!string.IsNullOrWhiteSpace(levelName))
                        np.StringFields["m_text"] = levelName;
                    if (TfBlockDeserializer.ExtractLevelElevation(
                        seg, bs, (uint)ds, cls) is double levelElevation)
                        np.DoubleFields["__levelElevation"] = levelElevation;
                }
                if (np is { Count: > 0 }) state.NativeParams[eid] = np;
                if (cls == 0x083C
                    && TfBlockDeserializer.ExtractGridDatum(seg, bs, (uint)ds, cls)
                        is { } gridDatum)
                {
                    state.GridDatums[eid] = new GridDatumData(
                        gridDatum.Name, gridDatum.StartPoint,
                        gridDatum.EndPoint, gridDatum.CutVector, gridDatum.LevelInterval, gridDatum.XVector, gridDatum.Envelope,
                        gridDatum.Origin, gridDatum.YVector);
                }
            }
            else if (state.R22View && rootClassBlock && TfBlockDeserializer.IsSubclassOf(cls, 1794) && state.NativeParams.TryGetValue(eid, out var fieldNp))
                AddDriveCurveFromRewrittenBlock(fieldNp, TfBlockDeserializer.ExtractParams(seg, bs, (uint)ds, cls));
            if (cls == 480) // BasePoint: internal + shared positions
            {
                var coordinates = TfBlockDeserializer.ExtractBasePointCoordinates(
                    seg, bs, (uint)ds, cls);
                if (coordinates is { } bp) state.BasePointCoordinates[eid] = bp;
            }
            if (cls == 1829 && (!state.FamilyCutLoops.ContainsKey(eid)
                    || !state.FamilySymbolCutLoopCounts.ContainsKey(eid)))
            {
                var cutLoops = TfBlockDeserializer.ExtractFamilySymbolCutLoops(
                    seg, bs, (uint)ds, cls, out int? cutLoopCount);
                if (cutLoops is { Count: > 0 } && !state.FamilyCutLoops.ContainsKey(eid))
                    state.FamilyCutLoops[eid] = cutLoops;
                if (cutLoopCount is int count)
                    state.FamilySymbolCutLoopCounts.TryAdd(eid, count);
            }
            List<(ushort ClassId, int ElementId, bool Cutout)>? cutOutSteps = null;
            if (TfBlockDeserializer.IsSubclassOf(cls, 363)
                && !state.HostCuttingElementIds.ContainsKey(eid)
                && (TfBlockDeserializer.ExtractCuttingElementIdsAndSteps(
                        seg, bs, (uint)ds, cls, out cutOutSteps)
                    ?? TfBlockDeserializer.ExtractCuttingElementIds(
                        seg, bs, (uint)ds, cls)) is { } cuttingElementIds)
            {
                state.HostCuttingElementIds[eid] = cuttingElementIds;
                if (cutOutSteps != null) state.HostCutOutSteps[eid] = cutOutSteps;
            }
            if (cutOutSteps == null && TfBlockDeserializer.IsSubclassOf(cls, 363)
                && !state.HostCutOutSteps.ContainsKey(eid)
                && TfBlockDeserializer.ExtractCutOutStepsWithLayoutSearch(seg, bs, (uint)ds, cls) is { } searchedSteps)
            {
                state.HostCutOutSteps[eid] = searchedSteps;
                state.HostCutOutStepsFromLayoutSearch.Add(eid);
            }
            // GRep geometry blocks (0x07D7 R22 / 0x07D6 R20): the per-chunk BlockParser
            // scan misses most blocks that cross chunk boundaries, so decode them here at
            // full coverage. Prefer the result with more real world faces (or an instance
            // ref) over whatever the chunk scan stored.
            if ((cls & 0xFFFF) is 0x07D7 or 0x07D6)
            {
                // Decoded in parallel after the walk; the first decodable block of an element wins, as before.
                if (!state.GrepBlocks.ContainsKey(eid))
                    walkGReps.Add((seg, bs, (uint)ds, eid));
            }
            // Sketch/detail curves at full coverage (safe: extraction has strict filters).
            if (cls == 0x0346 && !state.SketchCurves.ContainsKey(eid))
                BlockParser.ExtractSketchCurve(seg, bs + 14, bs + 16 + (int)ds, eid,
                    state.SketchCurves, state.DetailCurveStyles);
            // Full schema walk of the curve: recovers detail-arc style+length (the pattern
            // scan above only handles lines).
            if (cls == 0x0346 && !state.CurveGeometry.ContainsKey(eid))
            {
                var cg = TfBlockDeserializer.ExtractCurveGeometry(seg, bs, (uint)ds);
                if (cg is { } g) state.CurveGeometry[eid] = g;
            }
            // Deliberately NOT expanded here: CurveBlockOwners (uncategorized owners were mislabeled OST_Lines),
            // AnalyticalNodeOwners, registry category assignment.
        }, (seg, recordStart, eid) => ElementHeaderCategory.Record(state, seg, recordStart, eid));

        ApplyFirstDecodableGReps(state, walkGReps);

        // Second pass: geometry blocks the anchored walk missed. Family symbols live in
        // the family doc's own id space (absent from ElemTable), so whole symbol regions
        // fail to anchor, and ordinary INSTANCE blocks get skipped the same way. Targeted
        // signature scan for every id we know about that still lacks a GRep block: referenced
        // symbols, ElemTable ids, record ids. Ids that legitimately have no geometry
        // cost nothing (no block bytes match them).
        var missingIds = new HashSet<uint>();
        // This single targeted pass owns spatial-cache multiplicity. Do not also record
        // these blocks in the anchored/per-chunk passes or stop after the first success.
        foreach (var id in state.RoomNameSource.Keys) missingIds.Add(id);
        foreach (var g in state.GrepBlocks.Values)
            foreach (var instance in g.Instances)
                if (instance.SymbolElemId > 0 && !state.GrepBlocks.ContainsKey((uint)instance.SymbolElemId))
                    missingIds.Add((uint)instance.SymbolElemId);
        foreach (var id in state.AllKnownIds)
            if (!state.GrepBlocks.ContainsKey(id)) missingIds.Add(id);
        foreach (var id in state.Records.Keys)
            if (!state.GrepBlocks.ContainsKey(id)) missingIds.Add(id);
        // Referenced-but-recordless ids too: sketch-profile lines are often known ONLY via
        // their owner's reference list.
        foreach (var rec in state.Records.Values)
            if (rec.References != null)
                foreach (var r in rec.References)
                    if (r > 0 && !state.GrepBlocks.ContainsKey((uint)r)) missingIds.Add((uint)r);
        if (missingIds.Count > 0)
        {
            int foundSketch = 0;
            var scanGReps = new List<(byte[] Segment, int Start, uint Size, uint Eid)>();
            TfStreamWalker.ScanBlocks(state.Chunks, missingIds, (seg, bs, ds, cls, eid) =>
            {
                if (dumpGrepEids.Count > 0 && dumpGrepEids.Contains(eid))
                {
                    try
                    {
                        int len = (int)Math.Min(16L + ds, (long)seg.Length - bs);
                        var buf = new byte[len];
                        Array.Copy(seg, bs, buf, 0, len);
                        File.WriteAllBytes(Path.Combine(Path.GetTempPath(), $"grep_{eid}_{cls:X4}_scan.bin"), buf);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        DiagnosticSink.WriteLine($"Block dump failed: {ex.Message}");
                    }
                }
                if (cls == 0x0346)
                {
                    // Sketch/detail curve blocks: floor/slab profile lines whose blocks the
                    // anchored walk missed - hole loops depend on them.
                    if (!state.SketchCurves.ContainsKey(eid))
                    {
                        int before = state.SketchCurves.Count;
                        BlockParser.ExtractSketchCurve(seg, bs + 14, bs + 16 + (int)ds, eid,
                            state.SketchCurves, state.DetailCurveStyles);
                        if (state.SketchCurves.Count > before) foundSketch++;
                    }
                    if (!state.CurveGeometry.ContainsKey(eid))
                    {
                        var cg = TfBlockDeserializer.ExtractCurveGeometry(seg, bs, ds);
                        if (cg is { } g) state.CurveGeometry[eid] = g;
                    }
                    return;
                }
                if (state.RoomNameSource.ContainsKey(eid))
                    RecordSpaceBoundarySource(state, seg, bs, ds, cls, eid);
                if (state.GrepBlocks.ContainsKey(eid)) return;
                scanGReps.Add((seg, bs, ds, eid));
            });
            int found = ApplyFirstDecodableGReps(state, scanGReps);
            DiagnosticSink.WriteLine($"  [gscan] targeted scan recovered {found} G-blocks + {foundSketch} sketch curves for {missingIds.Count} candidate ids");
        }
        ReadOwnWallPlanarBodies(state);
    }

    /// <summary>Decodes collected GRep blocks in parallel and stores, in collection order, the first decodable block of
    /// each element that has none yet. Returns the number stored.</summary>
    private static int ApplyFirstDecodableGReps(
        ParsingState state, List<(byte[] Segment, int Start, uint Size, uint Eid)> jobs)
    {
        if (jobs.Count == 0) return 0;
        var decoded = new GRepData?[jobs.Count];
        bool slowDiag = Environment.GetEnvironmentVariable("RVTIO_GREP_SLOW") is { Length: > 0 };
        Parallel.For(0, jobs.Count, index =>
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            decoded[index] = GRepParserV2.Parse(jobs[index].Segment, jobs[index].Start, jobs[index].Size);
            if (slowDiag && (clock.ElapsedMilliseconds > 2000 || decoded[index]?.Faces.Count > 100000))
                DiagnosticSink.WriteLine($"slow GRep eid={jobs[index].Eid} ms={clock.ElapsedMilliseconds} faces={decoded[index]?.Faces.Count}");
        });
        int stored = 0;
        for (int index = 0; index < jobs.Count; index++)
        {
            if (decoded[index] is { } geometry && !state.GrepBlocks.ContainsKey(jobs[index].Eid))
            {
                state.GrepBlocks[jobs[index].Eid] = geometry;
                stored++;
            }
            decoded[index] = null;
        }
        return stored;
    }

    internal static void ParseBlocks(ParsingState state, TextWriter? diag)
    {
        var lap = System.Diagnostics.Stopwatch.StartNew();
        void Lap(string name) { diag?.WriteLine($"Timing: {name} {lap.Elapsed.TotalSeconds:F1}s"); lap.Restart(); }
        // Expand known IDs with discovered record IDs
        foreach (var eid in state.Records.Keys)
            state.AllKnownIds.Add(eid);

        // Name-table scan: authoritative element names + symbol-to-family chain
        foreach (var chunk in state.NonRecordChunks)
            NameTableScanner.Scan(chunk, state);
        foreach (var chunk in state.RecordChunks)
            NameTableScanner.Scan(chunk, state);
        diag?.WriteLine($"Name table: {state.ElementNameTable.Count} entries, {state.NameHolderTarget.Count} holders");

        Lap("name table");
        // Global shading-entry scan (holder id to color/shininess votes): fallback source for
        // material colors when the material's own 0x09B6 block is not decodable.
        foreach (var chunk in state.NonRecordChunks)
            BlockParser.CollectShadingEntries(chunk, state.ShadingEntries);
        foreach (var chunk in state.RecordChunks)
            BlockParser.CollectShadingEntries(chunk, state.ShadingEntries);

        Lap("shading entries");
        // Chunk-level HVAC load-type scan (classIdx- and block-walk-independent).
        foreach (var chunk in state.NonRecordChunks)
            BlockParser.CollectHvacTables(chunk, state.AllKnownIds, state.HvacSpaceTypeParams, state.HvacScheduleRefs);
        foreach (var chunk in state.RecordChunks)
            BlockParser.CollectHvacTables(chunk, state.AllKnownIds, state.HvacSpaceTypeParams, state.HvacScheduleRefs);

        Lap("hvac tables");
        // Level-elevation candidates (gated to OST_Levels at application time).
        foreach (var chunk in state.NonRecordChunks)
            BlockParser.CollectLevelElevations(chunk, state);
        foreach (var chunk in state.RecordChunks)
            BlockParser.CollectLevelElevations(chunk, state);

        Lap("level elevations");
        // BIP-keyed strings (per-element marks/descriptions/comments/keynotes).
        foreach (var chunk in state.NonRecordChunks)
            BlockParser.CollectBipStrings(chunk, state);
        foreach (var chunk in state.RecordChunks)
            BlockParser.CollectBipStrings(chunk, state);

        Lap("bip strings");
        // a file read by its framing or an unrewritten 64-bit file: skip the legacy 32-bit partition chunk scan
        bool framedBlocks = state.ReadsFramedRecords;
        bool skipPartitionScan = framedBlocks
            || (!state.R22View && state.WideSchema != null && WideElementRecords.IdWidth(state.WideSchema) == 8);
        var partitionChunks = skipPartitionScan
            ? state.Chunks.Take(state.PartitionChunkCount).ToHashSet(ReferenceEqualityComparer.Instance) : null;
        bool Scanned(byte[] chunk) => partitionChunks?.Contains(chunk) != true;
        // First pass: parse blocks, collect block-only IDs
        var firstPassGReps = new List<BlockParser.PendingGRep>();
        if (framedBlocks) ParseFramedBlocks(state, collectBlockOnlyIds: true, firstPassGReps);
        foreach (var chunk in state.NonRecordChunks.Where(Scanned))
            BlockParser.ParseChunk(chunk, state, collectBlockOnlyIds: true, deferred: firstPassGReps);
        foreach (var chunk in state.RecordChunks.Where(Scanned))
            BlockParser.ParseChunk(chunk, state, collectBlockOnlyIds: true, deferred: firstPassGReps);
        BlockParser.ApplyPendingGReps(state, firstPassGReps);
        diag?.WriteLine($"Block-only elements: {state.BlockOnlyIds.Count}");

        Lap("first parse pass");
        // Second pass: re-parse for block-only elements
        if (state.BlockOnlyIds.Count > 0)
        {
            foreach (var bid in state.BlockOnlyIds)
                state.AllKnownIds.Add(bid);
            var secondPassGReps = new List<BlockParser.PendingGRep>();
            if (framedBlocks) ParseFramedBlocks(state, collectBlockOnlyIds: false, secondPassGReps);
            foreach (var chunk in state.NonRecordChunks.Where(Scanned))
                BlockParser.ParseChunk(chunk, state, deferred: secondPassGReps);
            foreach (var chunk in state.RecordChunks.Where(Scanned))
                BlockParser.ParseChunk(chunk, state, deferred: secondPassGReps);
            BlockParser.ApplyPendingGReps(state, secondPassGReps);
        }
        Lap("second parse pass");
        diag?.WriteLine($"GRep blocks: {state.GrepBlocks.Count} elements");
        diag?.WriteLine($"FamilyInstance transforms: {state.FamilyInstanceTransforms.Count} elements");
        diag?.WriteLine($"Dimensions/constraints (0x091F+0x009D): {state.Dimensions.Count} elements");
        diag?.WriteLine($"Sketch-line curves (0x0346): {state.SketchCurves.Count} elements");
        diag?.WriteLine($"Material appearances (0x09B6): {state.MaterialAppearances.Count} materials");
        diag?.WriteLine($"HVAC space types (0x085A): {state.HvacSpaceTypeParams.Count} elements");
        if (state.ForgeSpecRegistry.Count > 0)
            diag?.WriteLine($"ForgeTypeId spec registry: {state.ForgeSpecRegistry.Count} entries");
        if (state.ForgeParamSpecs.Count > 0)
            diag?.WriteLine($"ForgeId to spec mappings: {state.ForgeParamSpecs.Count} params");

    }

    /// <summary>
    /// Scan all chunks for blocks with classIdx=0x00012100 (CompoundStructure) to extract wall function enum.
    /// BlockParser may miss these blocks if they span chunk boundaries; this scan uses direct byte matching.
    /// </summary>
    private static void ExtractWallFunctions(ParsingState state)
    {
        // classIdx bytes: 00 21 01 00 (little-endian 0x00012100)
        byte[] classIdxPattern = [0x00, 0x21, 0x01, 0x00];

        foreach (var chunk in state.NonRecordChunks.Concat(state.RecordChunks))
        {
            // Scan for the classIdx pattern at offset +12 from a potential block header
            for (int i = 0; i + 16 + 0x1C <= chunk.Length; i++)
            {
                if (chunk[i + 12] != classIdxPattern[0] || chunk[i + 13] != classIdxPattern[1]
                    || chunk[i + 14] != classIdxPattern[2] || chunk[i + 15] != classIdxPattern[3])
                    continue;

                uint eid = BitConverter.ToUInt32(chunk, i);
                if (!state.AllKnownIds.Contains(eid)) continue;

                uint dataSize = BitConverter.ToUInt32(chunk, i + 8);
                if (dataSize < 0x1C || dataSize > 500_000) continue;

                // Read function enum at data offset 0x18 (block header is 16 bytes)
                int dataStart = i + 16;
                if (dataStart + 0x1C > chunk.Length) continue;

                int funcVal = BitConverter.ToInt32(chunk, dataStart + 0x18);
                if (funcVal >= 0 && funcVal <= 5 && !state.WallTypeFunctions.ContainsKey(eid))
                    state.WallTypeFunctions[eid] = funcVal;
            }
        }
    }

    private static void ExtractForgeParamDefs(ParsingState state)
    {
        var uidRe = new Regex(
            @"^([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})-[0-9a-f]{8}$",
            RegexOptions.IgnoreCase);

        foreach (var (eid, strs) in state.ElemStrings)
        {
            var forgeStr = strs.FirstOrDefault(s => s.StartsWith("autodesk.parameter."));
            if (forgeStr != null)
            {
                // Prefer &-prefixed name (reliable marker), fallback to " - " heuristic
                var displayName = strs.FirstOrDefault(s => s.StartsWith('&') && s.Length > 1)
                    ?? strs.FirstOrDefault(s => StringScanner.IsValidParamName(s) && s.Contains(" - ") && s.Length <= 80)
                    ?? strs.FirstOrDefault(s => StringScanner.IsValidParamName(s));
                state.ForgeParamDefs[eid] = (forgeStr, StringScanner.NormalizeParamName(displayName));
            }
            if (state.DocGuid == null)
            {
                foreach (var s in strs)
                {
                    var m = uidRe.Match(s);
                    if (m.Success) { state.DocGuid = m.Groups[1].Value.ToLowerInvariant(); break; }
                }
            }
        }

        foreach (var (eid, strs) in state.ElemScanStrings)
        {
            if (state.ForgeParamDefs.ContainsKey(eid)) continue;
            var forgeStr = strs.FirstOrDefault(s => s.StartsWith("autodesk.parameter."));
            if (forgeStr != null)
            {
                var displayName = strs.FirstOrDefault(s => s.StartsWith('&') && s.Length > 1)
                    ?? strs.FirstOrDefault(s => StringScanner.IsValidParamName(s) && s.Contains(" - ") && s.Length <= 80)
                    ?? strs.FirstOrDefault(s => StringScanner.IsValidParamName(s));
                if (displayName == null && state.ElemStrings.TryGetValue(eid, out var blkStrs))
                    displayName = blkStrs.FirstOrDefault(s => s.StartsWith('&') && s.Length > 1) ?? blkStrs.FirstOrDefault(StringScanner.IsValidParamName);
                state.ForgeParamDefs[eid] = (forgeStr, StringScanner.NormalizeParamName(displayName));
            }
        }

        // Fill missing display names from scan strings
        foreach (var eid in state.ForgeParamDefs.Keys.ToList())
        {
            if (state.ForgeParamDefs[eid].DisplayName != null) continue;
            if (state.ElemScanStrings.TryGetValue(eid, out var scanStrs))
            {
                var name = scanStrs.FirstOrDefault(StringScanner.IsValidParamName);
                if (name != null)
                    state.ForgeParamDefs[eid] = (state.ForgeParamDefs[eid].ForgeId, StringScanner.NormalizeParamName(name));
            }
        }
    }


    /// <summary>Extract External param captions from FFFFFFFF+GUID+Caption pattern in raw chunk data.
    /// Also resolves GUID to elemId via record chunk [GUID:16][elemId:4] table.</summary>
    private static void ExtractExternalParamCaptionsFromRaw(ParsingState state, ref int applied, TextWriter? diag)
    {
        // Scan all chunks for [FFFFFFFF][header][GUID:16][00 00][nameLen:4][caption]
        var guidToCaption = new Dictionary<Guid, string>();
        foreach (var chunk in state.Chunks)
        {
            for (int s = 0; s + 40 <= chunk.Length; s++)
            {
                if (BitConverter.ToUInt32(chunk, s) != 0xFFFFFFFF) continue;
                for (int hdrOff = 4; hdrOff <= 14; hdrOff += 2)
                {
                    int guidOff = s + hdrOff;
                    if (guidOff + 22 > chunk.Length) continue;
                    int afterGuid = guidOff + 16;
                    if (chunk[afterGuid] != 0 || chunk[afterGuid + 1] != 0) continue;
                    uint nameLen = BitConverter.ToUInt32(chunk, afterGuid + 2);
                    if (nameLen < 3 || nameLen > 200) continue;
                    if (afterGuid + 6 + nameLen * 2 > chunk.Length) continue;
                    // Caption must start with U+2026 (bytes 26 20), & (26 00), or Cyrillic (hi=04)
                    byte nlo2 = chunk[afterGuid + 6], nhi = chunk[afterGuid + 7];
                    bool isPrefix = (nlo2 == 0x26 && (nhi == 0x20 || nhi == 0x00));
                    bool isCyrillic = nhi == 0x04;
                    bool isAsciiLetter = nhi == 0x00 && ((nlo2 >= 'A' && nlo2 <= 'Z') || (nlo2 >= 'a' && nlo2 <= 'z'));
                    if (!isPrefix && !isCyrillic && !isAsciiLetter) continue;
                    var guidBytes = new byte[16];
                    Array.Copy(chunk, guidOff, guidBytes, 0, 16);
                    var guid = new Guid(guidBytes);
                    if (guid == Guid.Empty) continue;
                    int ff = 0, zz = 0;
                    for (int b = 0; b < 16; b++) { if (guidBytes[b] == 0xFF) ff++; if (guidBytes[b] == 0x00) zz++; }
                    if (ff > 3 || zz > 10) continue;
                    var sb = new System.Text.StringBuilder();
                    for (int k = 0; k < (int)nameLen; k++)
                    {
                        byte lo = chunk[afterGuid + 6 + k * 2], hi = chunk[afterGuid + 6 + k * 2 + 1];
                        sb.Append(hi == 0x04 ? (char)(0x0400 | lo) : hi == 0x20 ? (char)(0x2000 | lo) : (char)lo);
                    }
                    string rawName = sb.ToString().Trim();
                    if (rawName.Length > 0 && (rawName[0] == '\u2026' || rawName[0] == '&')) rawName = rawName[1..];
                    if (!string.IsNullOrEmpty(rawName) && !guidToCaption.ContainsKey(guid))
                        guidToCaption[guid] = rawName;
                    break;
                }
            }
        }
        // Resolve GUID to elemId via record chunk table [GUID:16][elemId:4]
        var guidToElemId = new Dictionary<Guid, uint>();
        foreach (var (eid, g) in state.ParamDefGuids)
            guidToElemId.TryAdd(g, eid);
        foreach (var (eid, def) in state.ForgeParamDefs)
        {
            var g = BipHelpers.ParseGuidFromForgeId(def.ForgeId);
            if (g.HasValue) guidToElemId.TryAdd(g.Value, eid);
        }
        var captionPrefixes = BuildGuidPrefixSet(guidToCaption.Keys);
        foreach (var chunk in state.RecordChunks)
            MapRecordChunkGuids(chunk, guidToCaption, captionPrefixes, state.AllKnownIds, guidToElemId);
        int guidApplied = 0;
        foreach (var (guid, caption) in guidToCaption)
        {
            if (!guidToElemId.TryGetValue(guid, out var eid)) continue;
            if (!state.ForgeParamDefs.TryGetValue(eid, out var existing) || existing.DisplayName == null)
            {
                state.ForgeParamDefs[eid] = (existing.ForgeId ?? "", caption);
                guidApplied++;
                applied++;
            }
            state.ParamDefGuids.TryAdd(eid, guid);
        }
        diag?.WriteLine($"  GUID+Caption scan: {guidToCaption.Count} GUIDs, {guidToElemId.Count} mapped, {guidApplied} applied");
    }

    /// <summary>The first 8 bytes (as stored by <see cref="Guid"/> construction from bytes) of every GUID, as a cheap pre-filter.</summary>
    internal static HashSet<ulong> BuildGuidPrefixSet(IEnumerable<Guid> guids)
    {
        var set = new HashSet<ulong>();
        Span<byte> buf = stackalloc byte[16];
        foreach (var g in guids)
        {
            g.TryWriteBytes(buf);
            set.Add(System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(buf));
        }
        return set;
    }

    /// <summary>Scans one record chunk at every even offset for [GUID:16][elemId:4] where the GUID has a caption; the last match wins.
    /// No allocation per offset: a GUID can be a key only if its first 8 bytes are in <paramref name="prefixes"/>.</summary>
    internal static void MapRecordChunkGuids(byte[] chunk, Dictionary<Guid, string> guidToCaption, HashSet<ulong> prefixes,
        HashSet<uint> knownIds, Dictionary<Guid, uint> guidToElemId)
    {
        if (prefixes.Count == 0) return;
        for (int i = 0; i + 20 <= chunk.Length; i += 2)
        {
            if (!prefixes.Contains(System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(chunk.AsSpan(i, 8)))) continue;
            var g = new Guid(chunk.AsSpan(i, 16));
            if (!guidToCaption.ContainsKey(g)) continue;
            uint eid = BitConverter.ToUInt32(chunk, i + 16);
            if (!knownIds.Contains(eid) || eid < 100) continue;
            guidToElemId[g] = eid;
        }
    }

    /// <summary>Extract binary GUIDs for shared param definitions.
    /// Scans 0x0B39 blocks for [description][sentinel][header][GUID:16][name][ForgeId] pattern,
    /// and also parses Global/Latest GUID tables with [elemId:4][GUID:16] entries.</summary>
    private static void ExtractBinaryParamGuids(ParsingState state, TextWriter? diag)
    {
        int found = 0;

        foreach (var chunk in state.NonRecordChunks)
        {
            int pos = 0;
            while (pos + 16 <= chunk.Length)
            {
                uint beid = BitConverter.ToUInt32(chunk, pos);
                uint bhash = BitConverter.ToUInt32(chunk, pos + 4);
                int bsz = BitConverter.ToInt32(chunk, pos + 8);
                uint bci = BitConverter.ToUInt32(chunk, pos + 12);
                if (bsz < 0 || bsz > chunk.Length - pos - 16) break;

                if ((bci & 0xFFFF) == 0x0B39 && bsz >= 40 && state.AllKnownIds.Contains(beid))
                {
                    int dataStart = pos + 16;
                    int dataEnd = dataStart + bsz;
                    // 0x0B39 block format: [...description...][FFFFFFFF][N-byte hdr][GUID:16][00 00][nameLen:4][name][ForgeId]
                    // Find FFFFFFFF sentinel, then scan for GUID at various offsets
                    bool foundGuidInBlock = false;
                    for (int s = dataStart; s + 4 <= dataEnd - 30 && !foundGuidInBlock; s++)
                    {
                        if (BitConverter.ToUInt32(chunk, s) != 0xFFFFFFFF) continue;
                        // Try multiple header sizes (4-12 bytes after sentinel including the FFFFFFFF)
                        for (int hdrSize = 4; hdrSize <= 14 && !foundGuidInBlock; hdrSize += 2)
                        {
                            int guidOff = s + hdrSize;
                            if (guidOff + 16 + 6 > dataEnd) continue;
                            int afterGuid = guidOff + 16;
                            if (chunk[afterGuid] != 0 || chunk[afterGuid + 1] != 0) continue;
                            uint nameLen = BitConverter.ToUInt32(chunk, afterGuid + 2);
                            if (nameLen < 3 || nameLen > 200) continue;
                            if (afterGuid + 6 + nameLen * 2 > dataEnd) continue;
                            byte nhi = chunk[afterGuid + 7];
                            if (nhi != 0x00 && nhi != 0x04 && nhi != 0x20) continue;

                        var guidBytes = new byte[16];
                        Array.Copy(chunk, guidOff, guidBytes, 0, 16);
                        var guid = new Guid(guidBytes);
                        if (guid == Guid.Empty) continue;
                        int ffCnt = 0;
                        for (int b = 0; b < 16; b++) if (guidBytes[b] == 0xFF) ffCnt++;
                        if (ffCnt > 3) continue;

                        var sb = new System.Text.StringBuilder();
                        for (int k = 0; k < (int)nameLen; k++)
                        {
                            byte lo = chunk[afterGuid + 6 + k * 2];
                            byte hi = chunk[afterGuid + 6 + k * 2 + 1];
                            sb.Append(hi == 0x04 ? (char)(0x0400 | lo) : hi == 0x20 ? (char)(0x2000 | lo) : (char)lo);
                        }
                        string rawName = sb.ToString().Trim();
                        // Strip leading ellipsis (U+2026) or & prefix
                        if (rawName.Length > 0 && (rawName[0] == '\u2026' || rawName[0] == '&'))
                            rawName = rawName[1..];

                        if (!string.IsNullOrEmpty(rawName))
                        {
                            if (!state.ForgeParamDefs.TryGetValue(beid, out var existing) || existing.DisplayName == null)
                            {
                                string forgeId = existing.ForgeId ?? "";
                                state.ForgeParamDefs[beid] = (forgeId, rawName);
                            }
                        }
                        if (!state.ParamDefGuids.ContainsKey(beid))
                        {
                            state.ParamDefGuids[beid] = guid;
                            found++;
                        }
                        foundGuidInBlock = true;
                        }
                    }
                }
                pos += 16 + bsz;
            }
        }

        // Scan raw (non-block) portions of chunks for GUID+name pattern:
        // [FFFFFFFF][header][GUID:16][00 00][nameLen:4][name with U+2026 or & prefix]
        foreach (var chunk in state.NonRecordChunks)
        {
            for (int s = 0; s + 40 <= chunk.Length; s++)
            {
                if (BitConverter.ToUInt32(chunk, s) != 0xFFFFFFFF) continue;
                // Try various header sizes after sentinel
                for (int hdrOff = 4; hdrOff <= 14; hdrOff += 2)
                {
                    int guidOff = s + hdrOff;
                    if (guidOff + 22 > chunk.Length) continue;
                    int afterGuid = guidOff + 16;
                    if (chunk[afterGuid] != 0 || chunk[afterGuid + 1] != 0) continue;
                    uint nameLen = BitConverter.ToUInt32(chunk, afterGuid + 2);
                    if (nameLen < 3 || nameLen > 200) continue;
                    if (afterGuid + 6 + nameLen * 2 > chunk.Length) continue;
                    byte nlo = chunk[afterGuid + 6], nhi = chunk[afterGuid + 7];
                    // Name must start with U+2026 (bytes 26 20) or & (0x26 0x00) or Cyrillic (hi=0x04)
                    if (!((nlo == 0x26 && (nhi == 0x20 || nhi == 0x00)) || nhi == 0x04)) continue;

                    var guidBytes = new byte[16];
                    Array.Copy(chunk, guidOff, guidBytes, 0, 16);
                    var guid = new Guid(guidBytes);
                    if (guid == Guid.Empty) continue;
                    // Reject GUIDs with too many 0xFF bytes (false positives from sentinel regions)
                    int ffCount = 0;
                    for (int b = 0; b < 16; b++) if (guidBytes[b] == 0xFF) ffCount++;
                    if (ffCount > 3) continue;
                    // Reject GUIDs with too many zero bytes (likely false positives)
                    int zeroCount = 0;
                    for (int b = 0; b < 16; b++) if (guidBytes[b] == 0x00) zeroCount++;
                    if (zeroCount > 10) continue;

                    var sb = new System.Text.StringBuilder();
                    for (int k = 0; k < (int)nameLen; k++)
                    {
                        byte lo = chunk[afterGuid + 6 + k * 2];
                        byte hi = chunk[afterGuid + 6 + k * 2 + 1];
                        sb.Append(hi == 0x04 ? (char)(0x0400 | lo) : hi == 0x20 ? (char)(0x2000 | lo) : (char)lo);
                    }
                    string rawName = sb.ToString().Trim();
                    if (rawName.Length > 0 && (rawName[0] == '\u2026' || rawName[0] == '&'))
                        rawName = rawName[1..];

                    if (string.IsNullOrEmpty(rawName)) continue;

                    // Find the owning element: scan backwards for a known element ID in the chunk
                    uint ownerEid = 0;
                    for (int back = s - 4; back >= Math.Max(0, s - 600); back -= 2)
                    {
                        uint candidateId = BitConverter.ToUInt32(chunk, back);
                        if (candidateId > 200000 && candidateId < 400000 && state.AllKnownIds.Contains(candidateId))
                        {
                            // A history date is not a parameter-definition class.
                            if (state.IsParameterElement(candidateId))
                            { ownerEid = candidateId; break; }
                        }
                    }

                    if (ownerEid != 0)
                    {
                        if (!state.ParamDefGuids.ContainsKey(ownerEid))
                        {
                            state.ParamDefGuids[ownerEid] = guid;
                            found++;
                        }
                        if (!state.ForgeParamDefs.TryGetValue(ownerEid, out var existing) || existing.DisplayName == null)
                        {
                            string forgeId = existing.ForgeId ?? "";
                            state.ForgeParamDefs[ownerEid] = (forgeId, rawName);
                        }
                    }
                }
            }
        }

        diag?.WriteLine($"  Binary param GUIDs: {found} ({state.ParamDefGuids.Count} total)");
    }

    /// <summary>Resolve unit symbols for parameters using ForgeTypeId pipeline and BIP name heuristics.
    /// Chain: ForgeId to ForgeParamSpecs (0x0B3A), specType, unit category, symbol.</summary>
    /// <summary>Recover the active project-location transform. The dominant repeated
    /// Z-rotation is true north; the survey BasePoint stores matching internal and shared
    /// positions, from which the translation follows exactly.</summary>
    private static double[]? FindSharedTransform(
        IReadOnlyList<byte[]> chunks,
        IReadOnlyDictionary<uint, (double[] Internal, double[] Shared)> basePoints,
        IReadOnlyDictionary<uint, ElementRecord> records,
        TextWriter? diag)
    {
        // A general-angle Z-rotation matrix appears in ONE record per rotated family
        // instance (unique angle) AND in MANY records for the shared true-north (all the
        // site-plan geometry carries the same angle). Histogram the general angles; a
        // dominant one that clears a threshold IS the project north. Axis-aligned models
        // have no general-angle winner, in which case the identity rotation is used.
        static bool Unit(double v) => !double.IsNaN(v) && Math.Abs(v) <= 1.0000001;
        static bool AxisAligned(double s) =>
            Math.Abs(s) < 1e-4 || Math.Abs(Math.Abs(s) - 1) < 1e-4;
        var votes = new Dictionary<long, int>(); // angle (micro-radians) -> occurrences
        var representatives = new Dictionary<long, (double C, double S)>();
        foreach (var chunk in chunks)
        {
            // Block payloads are not guaranteed to be 8-byte aligned. Walk all eight
            // alignments so transforms shifted by strings or pointers are still found.
            for (int alignment = 0; alignment < 8; alignment++)
            for (int off = alignment; off + 72 <= chunk.Length; off += 8)
            {
                double c00 = BitConverter.ToDouble(chunk, off);
                double c01 = BitConverter.ToDouble(chunk, off + 8);
                if (!Unit(c00) || !Unit(c01) || AxisAligned(c01)) continue;
                double c02 = BitConverter.ToDouble(chunk, off + 16);
                double c10 = BitConverter.ToDouble(chunk, off + 24);
                double c11 = BitConverter.ToDouble(chunk, off + 32);
                double c12 = BitConverter.ToDouble(chunk, off + 40);
                double c20 = BitConverter.ToDouble(chunk, off + 48);
                double c21 = BitConverter.ToDouble(chunk, off + 56);
                double c22 = BitConverter.ToDouble(chunk, off + 64);
                if (Math.Abs(c02) > 1e-9 || Math.Abs(c12) > 1e-9 || Math.Abs(c20) > 1e-9
                    || Math.Abs(c21) > 1e-9 || Math.Abs(c22 - 1) > 1e-9) continue;
                if (Math.Abs(c00 * c00 + c01 * c01 - 1) > 1e-9) continue;
                if (Math.Abs(c10 + c01) > 1e-9 || Math.Abs(c11 - c00) > 1e-9) continue;
                long key = (long)Math.Round(Math.Atan2(c01, c00) * 1e6);
                votes.TryGetValue(key, out int count);
                votes[key] = count + 1;
                representatives.TryAdd(key, (c00, c01));
            }
        }

        double cc = 1, ss = 0;
        bool foundRotation = false;
        var ranked = votes.OrderByDescending(v => v.Value).ToList();
        int top = ranked.Count > 0 ? ranked[0].Value : 0;
        int runner = ranked.Count > 1 ? ranked[1].Value : 0;
        diag?.WriteLine($"Shared transform signals: basePoints={basePoints.Count}, rotations={votes.Count}, top={top}, runner={runner}");
        // Require a clear, well-supported winner: the shared north is carried by many
        // records; a stray furniture angle is not (and does not clear a 2.5x runner-up).
        if (top >= 8 && top * 2 >= runner * 5)
        {
            (cc, ss) = representatives[ranked[0].Key];
            foundRotation = true;
        }
        diag?.WriteLine($"SharedTransform diag: foundRotation={foundRotation} cc={cc:R} ss={ss:R} "
            + $"angleRad={Math.Atan2(ss, cc):R} votes={votes.Count} top={top} runner={runner}");
        if (diag != null && ranked.Count > 0)
        {
            var (topC, topS) = representatives[ranked[0].Key];
            diag.WriteLine($"SharedTransform diag: topWinner(rawVote) angleRad={Math.Atan2(topS, topC):R} "
                + $"c={topC:R} s={topS:R} count={ranked[0].Value}");
        }
        if (diag != null && ranked.Count > 1)
        {
            var (runnerC, runnerS) = representatives[ranked[1].Key];
            diag.WriteLine($"SharedTransform diag: runnerUp angleRad={Math.Atan2(runnerS, runnerC):R} "
                + $"c={runnerC:R} s={runnerS:R} count={ranked[1].Value}");
        }

        if (diag != null)
        {
            foreach (var kv in basePoints)
            {
                records.TryGetValue(kv.Key, out var rec);
                diag.WriteLine($"SharedTransform diag: basePoint id={kv.Key} category={rec?.CategoryId} "
                    + $"internal=[{string.Join(",", kv.Value.Internal.Select(v => v.ToString("R")))}] "
                    + $"shared=[{string.Join(",", kv.Value.Shared.Select(v => v.ToString("R")))}]");
            }
        }

        var surveyPoint = basePoints.FirstOrDefault(kv =>
            records.TryGetValue(kv.Key, out var record) && record.CategoryId == -2001272);
        if (surveyPoint.Value.Internal is { Length: >= 3 } internalPoint
            && surveyPoint.Value.Shared is { Length: >= 3 } sharedPoint)
        {
            double tx = sharedPoint[0] - (cc * internalPoint[0] - ss * internalPoint[1]);
            double ty = sharedPoint[1] - (ss * internalPoint[0] + cc * internalPoint[1]);
            double tz = sharedPoint[2] - internalPoint[2];
            diag?.WriteLine($"SharedTransform diag: internal=[{internalPoint[0]:R},{internalPoint[1]:R},{internalPoint[2]:R}]");
            diag?.WriteLine($"SharedTransform diag: shared=[{sharedPoint[0]:R},{sharedPoint[1]:R},{sharedPoint[2]:R}]");
            diag?.WriteLine($"SharedTransform diag: tx={tx:R} ty={ty:R} tz={tz:R} "
                + $"txNoRotation={sharedPoint[0] - internalPoint[0]:R} tyNoRotation={sharedPoint[1] - internalPoint[1]:R}");
            return [cc, ss, 0, -ss, cc, 0, 0, 0, 1.0, tx, ty, tz];
        }

        // Preserve the previous rotation-only behavior for files whose survey point is
        // absent or could not be decoded. With neither signal there is no shared transform.
        return foundRotation
            ? [cc, ss, 0, -ss, cc, 0, 0, 0, 1.0, 0, 0, 0.0]
            : null;
    }

    /// <summary>The active project location as the IFC site needs it: the internal-to-shared transform (the
    /// <see cref="RevitFileInfo.SharedTransform"/> layout), the GeoSite coordinates and the project base point's
    /// shared elevation (feet).</summary>
    internal sealed record ActiveProjectLocation(
        double[] SharedTransform, double? Latitude, double? Longitude, double ElevationFeet, string? PlaceName = null);

    /// <summary>Resolve the active project location exactly: ActiveGeoLocationTrackingElement names the active
    /// GeoLocation, whose Trf holds the angle to true north and the origin, and whose symbol is the GeoSite with
    /// the latitude and longitude. Revit maps an internal point P to shared coordinates as R(P - origin), R being
    /// the rotation by the angle, so the internal-to-shared transform is R with translation -R*origin; the base
    /// points are not involved. Null when the active location is absent, ambiguous, not a rotation about Z, or
    /// disagrees with the survey point's stored shared position (the caller then keeps its older fallback).</summary>
    internal static ActiveProjectLocation? ResolveActiveProjectLocation(ParsingState state, TextWriter? diag)
    {
        if (state.ActiveGeoLocations.Count != 1
            || !state.GeoLocations.TryGetValue(state.ActiveGeoLocations.Values.Single(), out var location))
        {
            diag?.WriteLine($"Active project location: unresolved (trackers={state.ActiveGeoLocations.Count}, locations={state.GeoLocations.Count})");
            return null;
        }
        var (m, o, symbolId) = location;
        const double Tolerance = 1e-9;
        if (m.Concat(o).Any(v => !double.IsFinite(v))
            || Math.Abs(m[2]) > Tolerance || Math.Abs(m[5]) > Tolerance
            || Math.Abs(m[6]) > Tolerance || Math.Abs(m[7]) > Tolerance || Math.Abs(m[8] - 1) > Tolerance
            || Math.Abs(m[0] * m[0] + m[1] * m[1] - 1) > Tolerance
            || Math.Abs(m[3] + m[1]) > Tolerance || Math.Abs(m[4] - m[0]) > Tolerance)
        {
            diag?.WriteLine("Active project location: Trf is not a rotation about Z");
            return null;
        }
        double c = m[0], s = m[1];
        // shared = R(internal - origin), R = [[c, -s], [s, c]]  =>  translation = -R * origin
        // (0.0 - x, not -x, so a zero component never becomes -0).
        double tx = 0.0 - (c * o[0] - s * o[1]);
        double ty = 0.0 - (s * o[0] + c * o[1]);
        double tz = 0.0 - o[2];

        // The survey point stores its own shared position; it must be the image of its internal position.
        var survey = state.BasePointCoordinates.FirstOrDefault(kv =>
            state.Records.TryGetValue(kv.Key, out var record) && record.CategoryId == -2001272);
        if (survey.Value.Internal is { Length: >= 3 } sInternal && survey.Value.Shared is { Length: >= 3 } sShared)
        {
            double dx = c * sInternal[0] - s * sInternal[1] + tx - sShared[0];
            double dy = s * sInternal[0] + c * sInternal[1] + ty - sShared[1];
            double dz = sInternal[2] + tz - sShared[2];
            if (Math.Max(Math.Abs(dx), Math.Max(Math.Abs(dy), Math.Abs(dz))) > 1e-6)
            {
                diag?.WriteLine($"Active project location: survey point disagrees by ({dx:R},{dy:R},{dz:R}) ft");
                return null;
            }
        }

        var projectBasePoint = state.BasePointCoordinates.FirstOrDefault(kv =>
            state.Records.TryGetValue(kv.Key, out var record) && record.CategoryId == -2001271);
        double elevation = projectBasePoint.Value.Internal is { Length: >= 3 } pInternal ? pInternal[2] + tz : 0;
        double? latitude = null, longitude = null;
        string? placeName = null;
        if (symbolId > 0 && state.GeoSites.TryGetValue((uint)symbolId, out var site)
            && site.Latitude is >= -Math.PI / 2 and <= Math.PI / 2 && site.Longitude is >= -Math.PI and <= Math.PI)
        {
            (latitude, longitude) = site;
            placeName = state.GeoSitePlaceNames.GetValueOrDefault((uint)symbolId);
        }
        diag?.WriteLine($"Active project location: angle={Math.Atan2(s, c) * 180 / Math.PI:F4}deg "
            + $"translation=({tx:R},{ty:R},{tz:R}) ft, lat/lon={latitude?.ToString("R") ?? "?"}/{longitude?.ToString("R") ?? "?"}, elevation={elevation:R} ft, place={placeName ?? "?"}");
        return new ActiveProjectLocation([c, s, 0, -s, c, 0, 0, 0, 1.0, tx, ty, tz], latitude, longitude, elevation, placeName);
    }

    private static void ResolveParameterUnits(RevitFileInfo rvtInfo, ParsingState state)
    {
        int resolved = 0;
        foreach (var elem in rvtInfo.Elements)
        {
            foreach (var param in elem.Parameters)
            {
                if (param.StorageType != StorageType.Double) continue;

                string? specType = null;

                // Try the ForgeId spec mapping (from 0x0B3A blocks)
                if (param.ForgeId != null && state.ForgeParamSpecs.TryGetValue(param.ForgeId, out var spec))
                    specType = spec;

                // 2. Resolve unit category from spec type or BIP name
                string? unitCat = specType != null ? GetUnitCategory(specType) : null;
                if (unitCat == null && param.Definition?.Name != null)
                    unitCat = GetUnitCategoryFromBipName(param.Definition.Name);

                if (unitCat == null) continue;

                param.SpecType = specType;
                var (symbol, factor) = GetUnitDisplay(unitCat);
                param.UnitSymbol = symbol;
                param.ConversionFactor = factor;
                resolved++;
            }
        }
    }

    /// <summary>Extract unit category from spec type URN.</summary>
    private static string? GetUnitCategory(string specType)
    {
        // Extract the measurement type from spec URN
        // e.g. "autodesk.spec.aec:length-2.0.0" gives "length"
        // e.g. "autodesk.spec.aec.electrical:current-1.0.0" gives "current"
        int colon = specType.LastIndexOf(':');
        if (colon < 0) return null;
        string name = specType[(colon + 1)..];
        int dash = name.IndexOf('-');
        return dash > 0 ? name[..dash] : name;
    }

    /// <summary>Infer unit category from BIP parameter name.</summary>
    private static string? GetUnitCategoryFromBipName(string bipName)
    {
        if (bipName.Contains("WIDTH") || bipName.Contains("HEIGHT") || bipName.Contains("LENGTH")
            || bipName.Contains("OFFSET") || bipName.Contains("THICKNESS") || bipName.Contains("DEPTH")
            || bipName.Contains("DISTANCE") || bipName.Contains("RADIUS") || bipName.Contains("PERIMETER"))
            return "length";
        if (bipName.Contains("AREA")) return "area";
        if (bipName.Contains("VOLUME")) return "volume";
        if (bipName.Contains("ANGLE")) return "angle";
        return null;
    }

    /// <summary>Get display unit symbol and feet-based conversion factor for a unit category.</summary>
    private static (string symbol, double factor) GetUnitDisplay(string unitCategory) => unitCategory switch
    {
        "length" or "barDiameter" or "cableTraySize" or "conduitSize" or "crackWidth"
            or "ductInsulationThickness" or "ductLiningThickness" or "ductSize"
            or "pipeInsulationThickness" or "pipeSize" or "pipeDimension"
            or "reinforcementCover" or "reinforcementLength" or "reinforcementSpacing"
            or "wireDiameter" or "displacement" or "sectionDimension"
            or "decimalSheetLength" or "sheetLength"
            => ("mm", 304.8),
        "area" or "crossSection" or "surfaceAreaPerUnitLength"
            => ("m\u00B2", 0.09290304),
        "volume" or "reinforcementVolume"
            => ("m\u00B3", 0.0283168466),
        "angle"
            => ("\u00B0", 180.0 / Math.PI),
        "number" or "factor" or "demandFactor" or "slope"
            => ("", 1.0),
        "temperature" or "colorTemperature"
            => ("\u00B0C", 1.0), // Internal is already in degrees Celsius for temperature
        "temperatureDifference"
            => ("\u0394\u00B0C", 1.0),
        "current" => ("A", 1.0),
        "potential" => ("V", 1.0),
        "power" or "wattage" or "coolingLoad" or "heatingLoad" or "heatGain" or "apparentPower"
            => ("W", 1.0),
        "frequency" => ("Hz", 1.0),
        "illuminance" => ("lx", 1.0),
        "luminousFlux" => ("lm", 1.0),
        "luminousIntensity" => ("cd", 1.0),
        "energy" => ("J", 1.0),
        "pressure" or "stress" => ("Pa", 1.0),
        "force" or "linearForce" or "areaForce" => ("N", 1.0),
        "moment" => ("N\u00B7m", 1.0),
        "mass" or "massPerUnitLength" => ("kg", 1.0),
        "density" or "massDensity" => ("kg/m\u00B3", 1.0),
        "velocity" or "airFlow" or "flow" => ("m/s", 1.0),
        _ => ("", 1.0),
    };

    /// <summary>Rewrite instance ElemParamStrings/ElemBipDoubles user-param entries to use the
    /// project param def ID instead of the binary "family-local" key found in 0x702 blocks.
    /// Looks up each instance's typeId in FamilyParamMaps.</summary>
    private static void RemapInstanceParamKeys(ParsingState state, TextWriter? diag)
    {
        if (state.FamilyParamMaps.Count == 0)
        {
            diag?.WriteLine("Family param maps: 0 (no 0x725 mapping tables found)");
            return;
        }
        int remappedStrings = 0, remappedDoubles = 0, droppedNoMap = 0;
        var candidateEids = new HashSet<uint>(state.ElemParamStrings.Keys);
        foreach (var eid in state.ElemBipDoubles.Keys) candidateEids.Add(eid);
        foreach (var eid in candidateEids)
        {
            // Find the actual project type/symbol ID. ElemTable has no type column,
            // so resolve via ELEM_TYPE_PARAM / SYMBOL_ID_PARAM extracted
            // from the same 0x702 block during BIP int extraction.
            uint symbolId = 0;
            if (state.ElemBipInts.TryGetValue(eid, out var bipInts))
            {
                if (bipInts.TryGetValue(-1002050, out int t1) && t1 > 0) symbolId = (uint)t1;
                else if (bipInts.TryGetValue(-1002000, out int t2) && t2 > 0) symbolId = (uint)t2;
                else if (bipInts.TryGetValue(-1002052, out int t3) && t3 > 0) symbolId = (uint)t3;
            }
            if (symbolId == 0) continue;
            if (!state.FamilyParamMaps.TryGetValue(symbolId, out var map)) continue;
            if (state.ElemParamStrings.TryGetValue(eid, out var strs))
            {
                var remapped = new Dictionary<uint, string>();
                foreach (var (binKey, val) in strs)
                {
                    if (map.TryGetValue(binKey, out uint projKey))
                    {
                        remapped[projKey] = val;
                        remappedStrings++;
                    }
                    else
                    {
                        // No mapping: keep original key (won't match any record body entry, but harmless)
                        remapped[binKey] = val;
                        droppedNoMap++;
                    }
                }
                state.ElemParamStrings[eid] = remapped;
            }
            // NOTE: doubles from 0x702 blocks are stored under the paramKey of the entry whose
            // sentinel directly follows the double bytes; that paramKey is already the project
            // ID for doubles. Strings use family-local keys and need the chain remap above.
        }
        diag?.WriteLine($"Family param maps: {state.FamilyParamMaps.Count} symbols; remapped {remappedStrings} strings, {remappedDoubles} doubles ({droppedNoMap} unmapped)");
    }

    private static void PrePropagateBipDoubles(ParsingState state, TextWriter? diag)
    {
        int propagated = 0;
        for (int pass = 0; pass < 3; pass++)
        {
            int added = 0;
            foreach (var (eid, rec) in state.Records)
            {
                if (state.ElemBipDoubles.ContainsKey(eid)) continue;
                if (rec.References.Count == 0) continue;
                Dictionary<int, double>? inherited = null;
                foreach (var refId in rec.References)
                {
                    if (refId == eid) continue;
                    if (!state.ElemBipDoubles.TryGetValue(refId, out var refDbls)) continue;
                    inherited ??= new Dictionary<int, double>();
                    foreach (var (k, v) in refDbls)
                    {
                        // Only propagate BuiltInParameter keys (negative), not user params (positive IDs)
                        if (k < 0)
                            inherited.TryAdd(k, v);
                    }
                }
                if (inherited != null && inherited.Count > 0)
                {
                    state.ElemBipDoubles[eid] = inherited;
                    added++;
                }
            }
            propagated += added;
            if (added == 0) break;
        }
        diag?.WriteLine($"Pre-propagated BIP doubles to {propagated} elements");

        // Propagate WALL_ATTR_WIDTH_PARAM from wall types to instances
        int wallWidthPropagated = 0;
        foreach (var (eid, rec) in state.Records)
        {
            if (rec.CategoryId != -2000011) continue;
            if (state.ElemBipDoubles.TryGetValue(eid, out var bd) && bd.ContainsKey(-1001000)) continue;
            foreach (var refId in rec.References)
            {
                if (refId == eid) continue;
                if (state.WallTypeWidths.TryGetValue(refId, out var tw))
                {
                    if (bd == null) state.ElemBipDoubles[eid] = bd = new Dictionary<int, double>();
                    bd[-1001000] = tw;
                    wallWidthPropagated++;
                    break;
                }
            }
        }
        if (wallWidthPropagated > 0)
            diag?.WriteLine($"Wall width propagated to {wallWidthPropagated} additional instances");
    }

    /// <summary>
    /// Scan all chunks and produce a complete block classIdx inventory.
    /// Returns a map: classIdx to (count, totalBytes, minSize, maxSize, sampleElemIds).
    /// Also returns record stats separately.
    /// </summary>
    internal static (Dictionary<uint, BlockClassInfo> blocks, int totalBlocks, long totalBlockBytes,
                    int totalRecords, long totalRecordBytes)
        BuildBlockMap(List<byte[]> chunks, HashSet<uint>? knownIds = null)
    {
        var map = new Dictionary<uint, BlockClassInfo>();
        int totalBlocks = 0, totalRecords = 0;
        long totalBlockBytes = 0, totalRecordBytes = 0;

        foreach (var chunk in chunks)
        {
            int pos = 0;
            while (pos + 16 <= chunk.Length)
            {
                uint eid = BitConverter.ToUInt32(chunk, pos);

                if (pos + 12 <= chunk.Length)
                {
                    uint rBodySize = BitConverter.ToUInt32(chunk, pos + 4);
                    ushort rMagic = BitConverter.ToUInt16(chunk, pos + 8);
                    if ((rMagic == 0x053C || rMagic == 0x04F8)
                        && ElementIdEncoding.IsSupported(eid)
                        && rBodySize > 0 && rBodySize < 500_000
                        && pos + 12 + (int)rBodySize <= chunk.Length)
                    {
                        totalRecords++;
                        totalRecordBytes += rBodySize;
                        pos += 12 + (int)rBodySize;
                        continue;
                    }
                }

                // Block format: [elemId:4][hash:4][dataSize:4][classIdx:4][data: dataSize bytes]
                if (eid == 0) { pos += 4; continue; }
                uint dataSize = BitConverter.ToUInt32(chunk, pos + 8);
                if (dataSize == 0 || dataSize > 1_000_000) { pos++; continue; }
                if (pos + 16 + (int)dataSize > chunk.Length) { pos++; continue; }

                // Validate against ElemTable when available; otherwise use the exact public
                // model representability range instead of a project-size ceiling.
                if (knownIds != null ? !knownIds.Contains(eid)
                    : !ElementIdEncoding.IsSupported(eid)) { pos++; continue; }

                uint classIdx = BitConverter.ToUInt32(chunk, pos + 12);
                totalBlocks++;
                totalBlockBytes += dataSize;

                if (!map.TryGetValue(classIdx, out var info))
                    map[classIdx] = info = new BlockClassInfo { ClassIdx = classIdx };
                info.Count++;
                info.TotalBytes += dataSize;
                if (dataSize < info.MinSize) info.MinSize = (int)dataSize;
                if (dataSize > info.MaxSize) info.MaxSize = (int)dataSize;
                if (info.SampleElemIds.Count < 5 && ElementIdEncoding.IsSupported(eid))
                    info.SampleElemIds.Add(eid);

                // Sample first 64 bytes of data for pattern analysis
                if (info.SampleData == null)
                {
                    int sampleLen = Math.Min(64, (int)dataSize);
                    info.SampleData = new byte[sampleLen];
                    Array.Copy(chunk, pos + 16, info.SampleData, 0, sampleLen);
                }

                pos += 16 + (int)dataSize;
            }
        }
        return (map, totalBlocks, totalBlockBytes, totalRecords, totalRecordBytes);
    }
}

/// <summary>Statistics for a single block classIdx value.</summary>
internal class BlockClassInfo
{
    public uint ClassIdx { get; set; }
    public int Count { get; set; }
    public long TotalBytes { get; set; }
    public int MinSize { get; set; } = int.MaxValue;
    public int MaxSize { get; set; }
    public List<uint> SampleElemIds { get; set; } = new();
    public byte[]? SampleData { get; set; }

    public string ClassIdxHex => $"0x{ClassIdx:X8}";
    public double AvgSize => Count > 0 ? (double)TotalBytes / Count : 0;
}
