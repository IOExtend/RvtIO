using System.IO.Compression;
using OpenMcdf;
using RvtIO;

namespace RvtIO.Parsing;

internal static class ElemTableReader
{
    /// <summary>Legacy diagnostic workset ids from the last two-argument Read (R22 28-byte records, field at
    /// offset 20). Non-zero only in workshared files; equal to ELEM_PARTITION_PARAM / Element.WorksetId.</summary>
    public static Dictionary<uint, uint> LastWorksets { get; private set; } = new();

    /// <summary>Legacy diagnostic raw ElemRec.History.CreationDate from the last two-argument Read (R22 28-byte
    /// records, field at offset 8). The 28-byte ElemTable record is the on-disk ElemRec structure
    /// - [m_id:4][m_history.m_originalElementId:4][m_history.m_creationDate:4]
    /// [m_history.m_lastModificationDate:4][m_history.m_lastUserModificationDate:4]
    /// [m_partitionId:4][m_OwningElementId:4] - where offset 4 (originalElementId) equals offset 0
    /// (id) for elements not copied from another element, offset 20 (partitionId) is the
    /// workset id above, and offset 24 defaults to the 0xFFFFFFFF
    /// InvalidElementId sentinel for un-owned elements.
    ///
    /// NOT wired into <see cref="RvtIO.Internal.ParsingState.ElemEpisodeNumbers"/>: the raw value is not a direct
    /// index into <see cref="RvtIO.Internal.ParsingState.EpisodeGuids"/>. It is offset by a per-document constant that
    /// cannot be derived from the data in the file, so using it directly would silently produce wrong UniqueIds rather
    /// than merely unresolved ones. The exact export ids read the table separately (<see cref="ElementExportIds"/>).</summary>
    public static Dictionary<uint, int> LastEpisodeNumbers { get; private set; } = new();

    public static Dictionary<uint, uint> Read(RootStorage root)
    {
        var result = Read(root, out var worksets, out var creationDates);
        LastWorksets = worksets;
        LastEpisodeNumbers = creationDates;
        return result;
    }

    // Production callers retain their own maps, never a later reader's legacy Last* values.
    public static Dictionary<uint, uint> Read(RootStorage root,
        out Dictionary<uint, uint> worksets, out Dictionary<uint, int> creationDates) =>
        Read(root, out worksets, out creationDates, out _);

    /// <param name="exact">True when the whole table was decoded with a verified page CRC and length, so an element
    /// without a row is known to be absent from the table (and not just lost to a damaged stream).</param>
    public static Dictionary<uint, uint> Read(RootStorage root,
        out Dictionary<uint, uint> worksets, out Dictionary<uint, int> creationDates, out bool exact,
        (int MaxBytes, int MaxIterations)? limits = null, RvtReadDiagnostics? diagnostics = null)
    {
        worksets = new();
        creationDates = new();
        exact = false;
        var result = new Dictionary<uint, uint>();
        try
        {
            var globalStor = root.OpenStorage("Global");
            using var et = globalStor.OpenStream("ElemTable");
            var data = new byte[et.Length];
            et.ReadExactly(data);

            if (PagedGzipStream.Decompress(data) is { } exactTable)
            {
                RecordStream(diagnostics, "Global/ElemTable", data.Length, 1, 1, 0, 0, 0);
                // Every version but R22: the layout comes from the file's own schema, not from a constant. The shipped R22 layout
                // is used only for a file whose release in force (the older of the saved and the schema release) is 2022 or unknown.
                var formats = FormatsSchema.TryRead(root);
                bool shipped = exactTable.Length >= 2 && BitConverter.ToUInt16(exactTable, 0) == ElementExportIds.ElemTableClassId
                    && SchemaRelease.AllowsShipped(formats?.Release ?? SchemaRelease.Of(root, null), 2022);
                if (exactTable.Length >= 2 && !shipped
                    && formats is { } schema && ElemTableLayout.TryParse(exactTable, schema) is { } rows)
                {
                    foreach (var row in rows)
                    {
                        // Id 0 is a real row (ProjectPhase / DimensionStyle) in ElemTable across wide and 32-bit schemas.
                        if (row.Id > uint.MaxValue || !(ElementIdEncoding.IsSupported((uint)row.Id) || row.Id == 0)) continue;
                        result[(uint)row.Id] = 0;
                        creationDates[(uint)row.Id] = row.CreationDate;
                        if (row.PartitionId >= 0) worksets[(uint)row.Id] = (uint)row.PartitionId;
                    }
                    exact = result.Count > 0;
                    return result;
                }
                if (exactTable.Length >= 2 && !shipped
                    && FileSchema.Read(root) is { } wideSchema
                    && WideElementRecords.ParseElemTable(exactTable, wideSchema) is { } wide)
                {
                    foreach (var (id, history) in wide.Histories)
                    {
                        result[id] = 0; // ElemRec holds history and ownership, not a class id.
                        creationDates[id] = history.CreationDate;
                    }
                    exact = result.Count > 0;
                    return result;
                }
                // The release in force has a layout other than the one the bytes have: no row is guessed.
                if (formats != null && ElemTableLayout.DisagreesWithRelease(formats)) return result;
                ParseRows(exactTable, result, out worksets, out creationDates);
                exact = result.Count > 0;
                return result;
            }

            int gzStart = -1;
            for (int ii = 0; ii < data.Length - 2; ii++)
                if (data[ii] == 0x1F && data[ii + 1] == 0x8B) { gzStart = ii; break; }
            if (gzStart < 0)
            {
                RecordStream(diagnostics, "Global/ElemTable", data.Length, 1, 0, 1, 0, 0);
                return result;
            }

            int gzLen = data.Length - gzStart;
            byte[]? decompressed = null;

            // Tier 1: standard GZipStream (correct for plain gzip).
            try
            {
                using var ms = new MemoryStream();
                using var gs = new GZipStream(new MemoryStream(data, gzStart, gzLen), CompressionMode.Decompress);
                gs.CopyTo(ms);
                decompressed = ms.ToArray();
            }
            // Plain gzip failed: the tolerant tiers below take over.
            catch (Exception ex) when (MalformedInput.Is(ex)) { decompressed = null; }

            // Tier 2: tolerant inflater. In some files GZipStream decodes the table into a subtly wrong
            // stream, while the bytes decoded up to the damaged block, with the trailer ignored, are the correct ones.
            // Prefer them over the managed Deflate64 decoder, and validate the GZipStream result against the gzip ISIZE trailer before trusting it.
            uint isize = BitConverter.ToUInt32(data, data.Length - 4);
            bool gzipLooksWrong = decompressed == null || decompressed.Length < 28
                || (isize != 0 && (uint)decompressed.Length != isize);
            if (gzipLooksWrong)
            {
                var tolerant = ChunkDecompressor.DecompressTolerant(data, gzStart, gzLen);
                if (tolerant is { Length: >= 28 }) decompressed = tolerant;
            }

            // Tier 3: managed Deflate64 (only if the tolerant inflater and gzip failed).
            if (decompressed == null || decompressed.Length < 28)
                decompressed = ChunkDecompressor.DecompressDeflate64Gzip(data, gzStart, gzLen);

            // Tier 4: bit-desync recovery. Revit's gzip encoder injects localized 1-2 bit corruption in some dynamic DEFLATE
            // blocks, and every standard decoder (GZipStream, tolerant inflater, managed Deflate64) stops at the first spurious
            // stored-block header with only part of the table. No cheap pre-check tells whether recovery is needed (the raw tail
            // is not a reliable gzip trailer, see ElemTableRecover.TryParseGzipHeader), so it is always attempted: Recover()
            // replays the deflate stream with structural relock past each desync and accepts the result only by CRC32 against
            // the member's real trailer, never by length. A CRC-validated result wins over an earlier tier; otherwise the earlier
            // result stands.
            int haveLen = decompressed?.Length ?? 0;
            var recovered = ElemTableRecover.Recover(data, gzStart, out var outcome, limits);
            if (outcome.LimitHit is { } hit)
                diagnostics?.LimitHits.Add(new ReaderLimitHit("Global/ElemTable", hit.Limit, hit.Value, Raisable: true,
                    $"the recovery of the damaged element table stopped at its limit of {hit.Value.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} ({outcome.Reason}); the table keeps what an earlier decoder produced ({haveLen.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} bytes)"));
            if (recovered != null)
            {
                bool sameAsEarlierTier = decompressed != null && recovered.AsSpan().SequenceEqual(decompressed);
                if (sameAsEarlierTier)
                    DiagnosticSink.WriteLine($"ElemTable: bit-desync recovery ran and confirmed the {haveLen} bytes an earlier tier already produced " +
                        $"(trailer@{outcome.TrailerOffset}, true ISIZE={outcome.TrueIsize}, true CRC=0x{outcome.TrueCrc:X8}, desyncs={outcome.DesyncCount})");
                else
                    DiagnosticSink.WriteLine($"ElemTable: bit-desync recovery {haveLen} -> {recovered.Length} bytes " +
                        $"(trailer@{outcome.TrailerOffset}, true ISIZE={outcome.TrueIsize}, true CRC=0x{outcome.TrueCrc:X8}, " +
                        $"desyncs={outcome.DesyncCount})");
                decompressed = recovered;
            }
            else
            {
                DiagnosticSink.WriteLine($"ElemTable: bit-desync recovery rejected: {outcome.Reason} " +
                    $"(have={haveLen}, recovered={outcome.RecoveredLength}, desyncs={outcome.DesyncCount}, " +
                    $"trailerFound={outcome.TrailerFound}" +
                    (outcome.TrailerFound ? $", trailer@{outcome.TrailerOffset}, true ISIZE={outcome.TrueIsize}, true CRC=0x{outcome.TrueCrc:X8}" : "") +
                    ")");
            }

            if (decompressed == null || decompressed.Length < 28)
            {
                DiagnosticSink.WriteLine($"ElemTable: decompression failed (raw={data.Length}, gzStart={gzStart})");
                RecordStream(diagnostics, "Global/ElemTable", data.Length, 1, 0, 1, 0, 0);
                return result;
            }

            // An unvalidated recovery must not replace the exact R22 table.
            if (decompressed.Length >= 2 && BitConverter.ToUInt16(decompressed) == ElementExportIds.ElemTableClassId)
            {
                DiagnosticSink.WriteLine("ElemTable: R22 stream failed exact paged CRC/length decoding");
                RecordStream(diagnostics, "Global/ElemTable", data.Length, 1, 0, 1, 0, 0);
                return result;
            }
            int rec = recovered != null && outcome.DesyncCount > 0 ? 1 : 0;
            int unverified = recovered == null ? 1 : 0;
            RecordStream(diagnostics, "Global/ElemTable", data.Length, 1, 1, 0, rec, unverified);
            ParseRows(decompressed, result, out worksets, out creationDates);

        }
        catch (Exception ex)
        {
            DiagnosticSink.WriteLine($"ElemTable: {ex.Message}");
            RecordStream(diagnostics, "Global/ElemTable", 0, 1, 0, 1, 0, 0);
        }
        return result;
    }

    private static void ParseDecompressedElemTable(byte[] decompressed, Dictionary<uint, uint> result)
    {
        ParseRows(decompressed, result, out var worksets, out var creationDates);
        LastWorksets = worksets;
        LastEpisodeNumbers = creationDates;
    }

    private static void ParseRows(byte[] decompressed, Dictionary<uint, uint> result,
        out Dictionary<uint, uint> worksets, out Dictionary<uint, int> creationDates)
    {
        worksets = new();
        creationDates = new();
        if (decompressed.Length >= 2 && BitConverter.ToUInt16(decompressed) == ElementExportIds.ElemTableClassId)
        {
            var histories = ElementExportIds.ParseElemTable(decompressed);
            if (histories == null) return;
            uint count = BitConverter.ToUInt32(decompressed, 2);
            for (int i = 0; i < count; i++)
            {
                int offset = 6 + i * ElementExportIds.ElemRecLength;
                uint id = BitConverter.ToUInt32(decompressed, offset);
                if (!ElementIdEncoding.IsSupported(id)) continue;
                result[id] = 0; // ElemRec contains history/ownership, not a type or class id.
                creationDates[id] = histories[id].CreationDate;
                int partition = BitConverter.ToInt32(decompressed, offset + 20);
                if (partition >= 0) worksets[id] = (uint)partition;
            }
            return;
        }
        // Preserve the existing legacy-format membership recovery after the exact R22 path.
        if (TryParse28ByteRecords(decompressed, result, out worksets, out creationDates)) return;
        TryParse40ByteRecords(decompressed, result);
    }

    /// <summary>R22/R20 ElemTable: 28-byte records. Layout: [eid:4][eid:4][z1:4][tid:4][tid:4][ws:4][FF:4].
    /// In workshared files ws (offset 20) is the element's WORKSET id (== Revit's
    /// ELEM_PARTITION_PARAM / Element.WorksetId); 0 in non-workshared files.</summary>
    private static bool TryParse28ByteRecords(byte[] d, Dictionary<uint, uint> result,
        out Dictionary<uint, uint> worksets, out Dictionary<uint, int> creationDates)
    {
        worksets = new();
        creationDates = new();
        const int RecordSize = 28;
        int headerSize = -1;
        for (int off = 0; off <= Math.Min(128, d.Length - RecordSize); off += 2)
        {
            uint e1 = BitConverter.ToUInt32(d, off);
            uint e2 = BitConverter.ToUInt32(d, off + 4);
            uint z1 = BitConverter.ToUInt32(d, off + 8);
            uint t1 = BitConverter.ToUInt32(d, off + 12);
            uint t2 = BitConverter.ToUInt32(d, off + 16);
            uint ff = BitConverter.ToUInt32(d, off + 24);
            if (e1 == e2 && e1 > 0 && e1 < 0xFFFFFF00 && z1 == 0 && t1 == t2 && t1 > 0 && ff == 0xFFFFFFFF)
            { headerSize = off; break; }
        }
        if (headerSize < 0) return false;

        for (int i = headerSize; i + RecordSize <= d.Length; i += RecordSize)
        {
            uint elemId = BitConverter.ToUInt32(d, i);
            uint lastModificationDate = BitConverter.ToUInt32(d, i + 12);
            if (elemId > 0 && elemId < 0xFFFFFF00 && lastModificationDate > 0) result[elemId] = 0;
        }

        worksets = ExtractWorksets(d, headerSize);
        creationDates = ExtractEpisodeNumbers(d, headerSize);
        return result.Count > 0;
    }

    /// <summary>Ordered walker for the workset column. Records mostly repeat every 28 bytes
    /// ([eid][eid][z][tid][rev][ws][FFFFFFFF]) with eids ascending, but some carry trailing
    /// reference lists and some sections use a packed encoding with fake dup-pairs - so each
    /// frame must be confirmed (FFFFFFFF tail, or a dup-pair right after) and the walk
    /// resyncs byte-by-byte when framing breaks. Real workset ids are small (&lt; 0x10000);
    /// larger values at offset 20 are mis-framed junk and are skipped.</summary>
    private static Dictionary<uint, uint> ExtractWorksets(byte[] d, int headerSize)
    {
        // Dense guarded scan. A flat record's workset u32 at +20 has byte23==0 and
        // byte22 in {0, 0x6D} (a flag), and the row is confirmed by an FFFFFFFF tail or a
        // dup-pair at +28. Values at +20 with byte23!=0 are packed GROUP REFERENCES
        // ([type:1B][handle:3B]) whose workset label lives in the external PartitionTable/.rws.
        var worksets = new Dictionary<uint, uint>();
        int n = d.Length;
        int i = 0;
        while (i + 28 <= n)
        {
            uint e = BitConverter.ToUInt32(d, i);
            if (e == 0 || e != BitConverter.ToUInt32(d, i + 4)) { i++; continue; }
            uint wsw = BitConverter.ToUInt32(d, i + 20);
            if ((wsw >> 24) != 0) { i++; continue; }              // packed ref, not a workset
            byte b22 = (byte)((wsw >> 16) & 0xFF);
            uint tail = BitConverter.ToUInt32(d, i + 24);
            bool nextDup = i + 36 <= n
                && BitConverter.ToUInt32(d, i + 28) != 0
                && BitConverter.ToUInt32(d, i + 28) == BitConverter.ToUInt32(d, i + 32);
            if ((b22 == 0 || b22 == 0x6D) && (tail == 0xFFFFFFFF || nextDup))
                worksets.TryAdd(e, wsw & 0xFFFF);
            i++;
        }
        return worksets;
    }

    /// <summary>Same dense guarded scan as <see cref="ExtractWorksets"/> (identical frame
    /// confirmation: a dup id pair at +0/+4, closed by an FFFFFFFF tail at +24 or a dup pair at
    /// +28), reading the raw ElemRec.History.CreationDate value at offset +8 instead of the
    /// workset id at +20. Episode-like values top out under 13,000, so a generous 100,000 ceiling rejects
    /// mis-framed junk without encoding a file-specific bound; a negative value is never valid and
    /// is rejected the same way. Internal (not private) so unit tests can feed synthetic 28-byte
    /// record buffers directly - see <see cref="LastEpisodeNumbers"/> for why the returned value
    /// is not yet usable as a direct table index.</summary>
    internal static Dictionary<uint, int> ExtractEpisodeNumbers(byte[] d, int headerSize)
    {
        var episodes = new Dictionary<uint, int>();
        int n = d.Length;
        int i = 0;
        while (i + 28 <= n)
        {
            uint e = BitConverter.ToUInt32(d, i);
            if (e == 0 || e != BitConverter.ToUInt32(d, i + 4)) { i++; continue; }
            int creationDate = BitConverter.ToInt32(d, i + 8);
            uint tail = BitConverter.ToUInt32(d, i + 24);
            bool nextDup = i + 36 <= n
                && BitConverter.ToUInt32(d, i + 28) != 0
                && BitConverter.ToUInt32(d, i + 28) == BitConverter.ToUInt32(d, i + 32);
            if (creationDate >= 0 && creationDate < 100_000 && (tail == 0xFFFFFFFF || nextDup))
                episodes.TryAdd(e, creationDate);
            i++;
        }
        return episodes;
    }

    /// <summary>R25 ElemTable: 40-byte records. Layout: [eid:4][f1:4][f2:4][clsA:4][clsB:4][eid:4][f6:4][refA:4][refB:4][f9:4]
    /// ALL IDs are stored shifted left by 16 bits (actualId = storedId >> 16).
    /// Header: [magic:u16][count:u16] = 4 bytes.
    /// clsA is a Formats/Latest schema class index (NOT a type element ID like R22).
    /// Only elements with eid &lt; 65536 can be stored (eid &lt;&lt; 16 must fit u32).</summary>
    private static bool TryParse40ByteRecords(byte[] d, Dictionary<uint, uint> result)
    {
        const int RecordSize = 40;
        int headerSize = -1;
        // Scan for first valid record: eid@0 == eid@20, zero@4, zero@24
        // All IDs are multiples of 0x10000 (shifted << 16)
        for (int off = 0; off <= Math.Min(128, d.Length - RecordSize); off += 2)
        {
            uint e1 = BitConverter.ToUInt32(d, off);
            uint z1 = BitConverter.ToUInt32(d, off + 4);
            uint clsA = BitConverter.ToUInt32(d, off + 12);
            uint e2 = BitConverter.ToUInt32(d, off + 20);
            uint z2 = BitConverter.ToUInt32(d, off + 24);
            if (e1 == e2 && e1 > 0 && e1 < 0xFFFFFF00 && z1 == 0 && z2 == 0 && clsA > 0
                && (e1 & 0xFFFF) == 0) // R25: IDs are multiples of 0x10000
            { headerSize = off; break; }
        }
        if (headerSize < 0) return false;

        for (int i = headerSize; i + RecordSize <= d.Length; i += RecordSize)
        {
            uint rawElemId = BitConverter.ToUInt32(d, i);
            if (rawElemId == 0 || rawElemId >= 0xFFFFFF00) continue;

            uint elemId = rawElemId >> 16;
            if (elemId == 0) continue;

            // clsA (offset 12) is a schema class index, not a type element ID.
            // Store with typeId=0 to register the element without implying type linkage.
            // Type-instance relationships in R25 are resolved through body references.
            result.TryAdd(elemId, 0);
        }
        return result.Count > 0;
    }

    private static void RecordStream(RvtReadDiagnostics? diagnostics, string name, long rawBytes, int members, int decoded, int failed, int recovered, int unverified)
    {
        if (diagnostics == null) return;
        if (diagnostics.Streams.Any(s => s.Name == name)) return;
        diagnostics.Streams.Add(new StreamRecord(name, rawBytes, members, decoded, failed, recovered, unverified));
    }
}
