using System.Text;
using RvtIO.Internal;
using RvtIO.Lookup;

namespace RvtIO.Parsing;

internal static class RecordParser
{
    internal static bool ContainsAnchoredR22Record(byte[] chunk, IReadOnlySet<uint> knownIds)
    {
        for (int position = 0; position + 12 <= chunk.Length; position++)
        {
            if (!TryReadR22Header(chunk, position, out uint elementId, out int recordSize))
                continue;
            int next = position + recordSize;
            if (knownIds.Contains(elementId)
                || (next == chunk.Length
                    ? position == 0
                    : TryReadR22Header(chunk, next, out _, out _)))
                return true;
        }
        return false;
    }

    internal static bool ContainsAnchoredR25Record(byte[] chunk, IReadOnlySet<uint> knownIds)
    {
        for (int position = 0; position + 20 <= chunk.Length; position++)
        {
            if (!TryReadR25Header(chunk, position, out uint elementId, out int recordSize))
                continue;
            int next = position + recordSize;
            if (knownIds.Contains(elementId)
                || (next == chunk.Length
                    ? position == 0
                    : TryReadR25Header(chunk, next, out _, out _)))
                return true;
        }
        return false;
    }

    public static void ParseChunk(byte[] chunk, ParsingState state, PartitionChunks? source = null, int segment = 0)
    {
        int pos = 0;
        bool aligned = false;
        while (pos + 12 <= chunk.Length)
        {
            if (TryReadR22Header(chunk, pos, out uint elemId, out int recordSize))
            {
                int next = pos + recordSize;
                // When resynchronizing, a record is anchored either by an element id
                // authenticated by ElemTable/a well-formed block, or by the next complete
                // record at the exact byte following this one. Once aligned, records are
                // consumed only at their declared boundaries, never by scanning bodies.
                if (!aligned && !state.AllKnownIds.Contains(elemId)
                    && !(next == chunk.Length ? pos == 0
                        : TryReadR22Header(chunk, next, out _, out _)))
                {
                    pos++;
                    continue;
                }
                uint bodySize = BitConverter.ToUInt32(chunk, pos + 4);
                if (!state.Records.ContainsKey(elemId))
                {
                    ushort subclass = BitConverter.ToUInt16(chunk, pos + 10);
                    var body = new byte[bodySize];
                    Array.Copy(chunk, pos + 12, body, 0, (int)bodySize);
                    var rec = new ElementRecord { ElemId = elemId, Body = body, Subclass = subclass };
                    ExtractReferences(rec);
                    ExtractBodyParams(rec);
                    BlockParser.ExtractBlockStrings(body, 0, body.Length, elemId, state.ElemStrings);
                    BlockParser.ExtractBlockDoubles(body, 0, body.Length, elemId, state.ElemDoubles);
                    state.Records[elemId] = rec;
                    // Extract after storing record (needs references for SymbolId validation)
                    BlockParser.ExtractFamilyInstanceTransform(body, 0, body.Length, elemId, state.FamilyInstanceTransforms, state.Records);
                    if (source?.StoredBlock(segment, pos + 12, body.Length) is { } stored)
                        rec.RetainBodyInScratch(stored);
                }
                pos = next;
                aligned = true;
                continue;
            }
            aligned = false;
            pos++;
        }
    }

    /// <summary>Parse R25 records: [elemId:4][zero:4][hash:4][bodySize:4][body][bodySize:4]</summary>
    public static void ParseChunkR25(byte[] chunk, ParsingState state, PartitionChunks? source = null, int segment = 0)
    {
        int pos = 0;
        bool aligned = false;
        while (pos + 20 <= chunk.Length)
        {
            if (TryReadR25Header(chunk, pos, out uint elemId, out int recordSize))
            {
                int next = pos + recordSize;
                if (!aligned && !state.AllKnownIds.Contains(elemId)
                    && !(next == chunk.Length ? pos == 0
                        : TryReadR25Header(chunk, next, out _, out _)))
                {
                    pos++;
                    continue;
                }
                uint bodySize = BitConverter.ToUInt32(chunk, pos + 12);
                if (!state.Records.ContainsKey(elemId))
                {
                    var body = new byte[bodySize];
                    Array.Copy(chunk, pos + 16, body, 0, (int)bodySize);
                    var rec = new ElementRecord { ElemId = elemId, Body = body, Subclass = 0 };
                    ExtractR25Body(rec);

                    BlockParser.ExtractBlockStrings(body, 0, body.Length, elemId, state.ElemStrings);
                    BlockParser.ExtractBlockDoubles(body, 0, body.Length, elemId, state.ElemDoubles);
                    state.Records[elemId] = rec;
                    if (source?.StoredBlock(segment, pos + 16, body.Length) is { } stored)
                        rec.RetainBodyInScratch(stored);
                }
                pos = next;
                aligned = true;
                continue;
            }
            aligned = false;
            pos++;
        }
    }

    private static bool TryReadR22Header(byte[] chunk, int position,
        out uint elementId, out int recordSize)
    {
        elementId = 0;
        recordSize = 0;
        if (position < 0 || position + 12 > chunk.Length) return false;
        uint candidateId = BitConverter.ToUInt32(chunk, position);
        uint bodySize = BitConverter.ToUInt32(chunk, position + 4);
        ushort magic = BitConverter.ToUInt16(chunk, position + 8);
        if (!ElementIdEncoding.IsSupported(candidateId)
            || magic is not (0x053C or 0x04F8)
            || bodySize is 0 or >= 500_000
            || position + 12L + bodySize > chunk.Length)
            return false;
        elementId = candidateId;
        recordSize = 12 + (int)bodySize;
        return true;
    }

    private static bool TryReadR25Header(byte[] chunk, int position,
        out uint elementId, out int recordSize)
    {
        elementId = 0;
        recordSize = 0;
        if (position < 0 || position + 20 > chunk.Length) return false;
        uint candidateId = BitConverter.ToUInt32(chunk, position);
        uint bodySize = BitConverter.ToUInt32(chunk, position + 12);
        if (!ElementIdEncoding.IsSupported(candidateId)
            || BitConverter.ToUInt32(chunk, position + 4) != 0
            || bodySize is <= 2 or >= 500_000
            || position + 20L + bodySize > chunk.Length
            || BitConverter.ToUInt32(chunk, position + 16 + (int)bodySize) != bodySize)
            return false;
        elementId = candidateId;
        recordSize = 20 + (int)bodySize;
        return true;
    }

    /// <summary>R25 Tf classId -> BuiltInCategory mapping.
    /// These are body topClassId values, NOT Formats/Latest rawIdx - they come from a combined
    /// global registry (Formats/Latest + ContentDocuments + runtime classes).
    /// Body prefix format: [topCid:2][0:4][FFFFFFFF:4] repeating at 10-byte stride.</summary>
    private static readonly Dictionary<ushort, int> R25ClassIdToCategory = new()
    {
        [0x1043] = -2000011, // OST_Walls (VWall - "Wall", "Glazed")
        [0x09B7] = -2000240, // OST_Levels (Level - "Level 1", "Roof Line")
        [0x09B8] = -2000220, // OST_Grids (Grid)
        [0x0E35] = -2001330, // OST_StructuralColumns (Column instances - "M_L-Anglel-Column")
    };

    /// <summary>Extract data from R25 record body (hierarchical Tf class sections).
    /// Body format: series of [TfClassId:u16][version:u32][fields...] sections.
    /// 0x02EF = Element base class marker: [0x02EF][ver=1][selfId:4][0:4][FFFFFFFF FFFFFFFF FFFFFFFF]
    /// then [assocLevelId:4][0:4][famId:4][0:4] etc. (8-byte element refs)</summary>
    private static void ExtractR25Body(ElementRecord rec)
    {
        var body = rec.Body;
        if (body.Length < 4) return;

        ushort topClassId = BitConverter.ToUInt16(body, 0);
        rec.Subclass = topClassId; // Store top-level Tf classId as subclass for classification

        if (R25ClassIdToCategory.TryGetValue(topClassId, out int catFromClass))
            rec.CategoryId = catFromClass;

        // Pattern: EF 02 [version:4=01000000] [selfId:4] [zero:4]
        int elemBasePos = -1;
        for (int i = 0; i + 10 <= body.Length; i += 2)
        {
            if (BitConverter.ToUInt16(body, i) == 0x02EF)
            {
                uint ver = BitConverter.ToUInt32(body, i + 2);
                if (ver == 1)
                {
                    uint selfId = BitConverter.ToUInt32(body, i + 6);
                    if (selfId == rec.ElemId || ElementIdEncoding.IsSupported(selfId))
                    { elemBasePos = i; break; }
                }
            }
        }

        if (elemBasePos >= 0)
        {
            // [0x02EF:2][ver:4][selfId:4][pad:4] = 14 bytes header
            // Then: [FFFFFFFF:4][FFFFFFFF:4][FFFFFFFF:4] (3 null refs) = 12 bytes
            // Then: element references as [id:4][pad:4] pairs
            int off = elemBasePos + 14; // past marker + ver + selfId + pad

            if (off + 12 <= body.Length)
            {
                off += 12;

                // Next fields are 8-byte element references
                // [assocLevelId:4][0:4] [famId:4][0:4] ...
                for (int r = 0; r < 8 && off + 8 <= body.Length; r++)
                {
                    uint refId = BitConverter.ToUInt32(body, off);
                    uint pad = BitConverter.ToUInt32(body, off + 4);
                    if (ElementIdEncoding.IsSupported(refId) && refId != 0xFFFFFFFF && pad == 0 && refId != rec.ElemId)
                    {
                        if (!rec.References.Contains(refId))
                            rec.References.Add(refId);
                    }
                    off += 8;
                }
            }
        }

        // R25 uses [elemId:u32][zero:u32], with FFFFFFFF FFFFFFFF for null
        for (int i = 0; i + 8 <= body.Length; i += 2)
        {
            uint refId = BitConverter.ToUInt32(body, i);
            uint pad = BitConverter.ToUInt32(body, i + 4);
            if (ElementIdEncoding.IsSupported(refId) && pad == 0 && refId != rec.ElemId && refId != 0xFFFFFFFF)
            {
                if (!rec.References.Contains(refId))
                    rec.References.Add(refId);
            }
        }

        if (topClassId == 0 && body.Length >= 6)
        {
            int catId = BitConverter.ToInt32(body, 2);
            if (catId < -1 && catId > -3_000_000)
                rec.CategoryId = catId;
        }

        // Category is a negative int32 in range (-3M, -1), typically after element refs
        if (rec.CategoryId == null && elemBasePos >= 0)
        {
            int searchStart = elemBasePos + 14;
            int searchEnd = Math.Min(body.Length - 4, searchStart + 200);
            for (int i = searchStart; i <= searchEnd; i += 2)
            {
                int val = BitConverter.ToInt32(body, i);
                if (val < -1 && val > -3_000_000)
                {
                    if (val <= -2000000 && val >= -2010000)
                    {
                        rec.CategoryId = val;
                        break;
                    }
                }
            }
        }

        // A record body has no anonymous bounding-box field that can be identified by
        // scanning for six doubles. Geometry bounds are decoded from the named
        // GRep.m_bBox / GRep.m_tightbBox schema fields instead.
    }

    public static void ExtractReferences(ElementRecord rec)
    {
        var body = rec.Body;
        int markerPos = -1;
        for (int i = 0; i < body.Length - 2; i++)
        {
            if (BitConverter.ToUInt16(body, i) == 0x0548)
            { markerPos = i; break; }
        }

        int refStart;
        if (markerPos >= 0)
        {
            refStart = markerPos + 2;
        }
        else
        {
            // R20: no 0x0548 marker - find geometry doubles after entry section, then refs after doubles
            refStart = FindPostEntryGeomOffset(body);
            if (refStart < 0) return;
        }

        if (refStart + 4 > body.Length) return;
        int count = BitConverter.ToInt32(body, refStart);

        if (count < 0 || count > 10000 || refStart + 4 + count * 4 > body.Length)
        {
            refStart += 48; // skip geometry doubles
            if (refStart + 4 > body.Length) return;
            count = BitConverter.ToInt32(body, refStart);
            if (count < 0 || count > 10000 || refStart + 4 + count * 4 > body.Length) return;
        }

        while (refStart + 4 <= body.Length)
        {
            count = BitConverter.ToInt32(body, refStart);
            if (count < 0 || count > 10000 || refStart + 4 + count * 4 > body.Length) break;
            refStart += 4;
            for (int i = 0; i < count; i++)
            {
                uint refId = BitConverter.ToUInt32(body, refStart + i * 4);
                if (refId > 0 && refId < 0xFFFFFF00) rec.References.Add(refId);
            }
            refStart += count * 4;
            if (refStart + 4 > body.Length) break;
            int nextCount = BitConverter.ToInt32(body, refStart);
            if (nextCount < 0 || nextCount > 10000) break;
        }
    }

    /// <summary>
    /// For R20 bodies without 0x0548 marker: find the offset where geometry doubles start
    /// by scanning past 14-byte entries until sentinel, then looking for valid doubles.
    /// Returns the offset of the first valid double, or -1.
    /// </summary>
    private static int FindPostEntryGeomOffset(byte[] body)
    {
        for (int pos = 2; pos + 14 <= body.Length; pos += 14)
        {
            int classId = BitConverter.ToUInt16(body, pos + 10);
            if (classId == 0 || classId == 0xFFFF)
            {
                for (int skip = pos + 14; skip + 48 <= body.Length; skip += 2)
                {
                    double d0 = BitConverter.ToDouble(body, skip);
                    if (!double.IsNaN(d0) && !double.IsInfinity(d0) && Math.Abs(d0) > 1e-6 && Math.Abs(d0) < 1e6)
                        return skip;
                }
                return -1;
            }
        }
        return -1;
    }

    public static void ExtractBodyParams(ElementRecord rec)
    {
        var body = rec.Body;
        if (body.Length < 4) return;

        int markerPos = -1;
        for (int i = 0; i < body.Length - 1; i++)
            if (BitConverter.ToUInt16(body, i) == 0x0548) { markerPos = i; break; }

        int entryLimit = markerPos >= 2 ? markerPos : body.Length;

        // Entry layout: [value:4][paramKey:4][fieldType:2][classId:2][flags:2]
        for (int pos = 2; pos + 14 <= entryLimit; pos += 14)
        {
            int rawValue = BitConverter.ToInt32(body, pos);
            int paramKey = BitConverter.ToInt32(body, pos + 4);
            int fieldType = BitConverter.ToUInt16(body, pos + 8);
            int classId = BitConverter.ToUInt16(body, pos + 10);
            if (classId == 0 || classId == 0xFFFF)
            {
                // CategoryId is stored as int32 right after the last valid 14-byte entry
                // When classId=0xFFFF, the entry itself contains: [categoryId:4][FF:4][FFFF:2][FFFF:2][FFFF:2]
                if (classId == 0xFFFF && rawValue < -1 && rawValue > -3_000_000)
                    rec.CategoryId = rawValue;
                break;
            }

            rec.EntryValues.Add(rawValue);

            if (paramKey == -1)
            {
                if (rawValue != -1 && (fieldType == 3 || fieldType == 4))
                {
                    rec.BipParams.Add(new BipEntry(0, null, fieldType, rawValue));
                }
                continue;
            }

            // Filter out garbage keys:
            // - Negative keys outside valid BIP range (-2M..-1)
            // - 0xFFFFxxxx keys (classIdx values masquerading as paramKey)
            // - Positive keys that cannot be represented by the public ElementId model
            if (paramKey < 0 && (paramKey < -2_000_000 || paramKey >= -1 || (paramKey & unchecked((int)0xFFFF0000)) == unchecked((int)0xFFFF0000)))
                continue;
            BipNames.Lookup.TryGetValue(paramKey, out string? bipName);
            // Keep -1 as a valid value (InvalidElementId in Revit) instead of converting to null.
            // Only treat 0xFFFFFFFF as null for non-paramKey entries (schema values).
            int? intVal = rawValue;
            // ft=205 entries mark user params as writable (non-readonly)
            if (fieldType == 205 && paramKey > 0)
                rec.WritableParamIds.Add(paramKey);
            rec.BipParams.Add(new BipEntry(paramKey, bipName, fieldType, intVal));
        }

        // Do not infer geometry from bytes following class id 0x0548
        // (ElementParents). That data is a class payload, not a bounding box.
    }
}
