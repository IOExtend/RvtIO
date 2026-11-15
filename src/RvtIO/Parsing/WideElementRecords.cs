namespace RvtIO.Parsing;

/// <summary>
/// Element table and element records of the releases whose element ids are 64-bit (the Revit 2022 layout stays in
/// <see cref="ElementExportIds"/> and <see cref="ElementHeaderCategory"/>). Every layout fact is read from the file's own
/// class schema (<see cref="FileSchema"/>) by class and field name; where the schema does not declare the expected shape
/// the result is null and nothing is guessed.
///
/// Global/ElemTable: u16 ElemTable class id, u32 count, then count ElemRec. ElemRec is the fixed concatenation of its
/// schema fields (m_history{m_originalElementId, m_creationDate, m_lastModificationDate, m_lastUserModificationDate},
/// m_id, m_OwningElementId, m_partitionId); the rows are in strictly increasing m_id order. Revit's UniqueId of a row is
/// the episode GUID episodes[count - 1 - m_creationDate] and m_originalElementId (see <see cref="ElementExportIds"/>).
///
/// Partition stream: read by its framing (<see cref="PartitionFraming"/>) where it holds, the header records being the channel-101
/// records and the blocks the channel-102 records of the project's own block; otherwise back-to-back items found by a
/// position-by-position walk. Every item opens with the 8-byte element id and closes with a 4-byte echo of its length, which is
/// what validates a candidate:
///   record  [id:8][size:4][ElementHeader class id:2 ...][size:4]                 (item length 16 + size)
///   block   [id:8][u32:4][size:4][class id:2 ...][size:4]                        (item length 20 + size)
/// The ElementHeader record opens with m_regenHistory (u32 count + count entries of the schema-declared fixed size), then
/// m_categroryId. The root class of an element is the class of its block whose class derives from Element and that lies
/// in a partition stream holding a header record of the same id (the stream also holds unrelated look-alike items, so a
/// foreign Element-derived block can sit in another stream). A value counts only when every record (block) of the id
/// agrees on it.
/// </summary>
internal static class WideElementRecords
{
    internal sealed record Result(
        Dictionary<uint, ElemRecHistory> Histories,
        Dictionary<uint, int> OwningElementIds,
        int SkippedRows);

    /// <summary>Byte width of the element id of this schema (ElemRec.m_id): 4 up to Revit 2023, 8 from Revit 2024; null otherwise.</summary>
    internal static int? IdWidth(FileSchema schema)
        => schema.ByName("ElemRec") is { } rec && FileSchema.SerializedFields(rec).FirstOrDefault(f => f.Name == "m_id") is { } id
           && schema.FieldSize(id) is (4 or 8) and int width && (SchemaRelease.IdWidth(schema.Release) is not int expected || expected == width) ? width : null;

    private static ulong ReadId(byte[] data, int at, int width)
        => width == 8 ? BitConverter.ToUInt64(data, at) : BitConverter.ToUInt32(data, at);

    /// <summary>Element id to ElemRec history and owner from a decoded Global/ElemTable stream, or null when the
    /// schema does not declare the 64-bit ElemRec layout or the stream does not match it.</summary>
    internal static Result? ParseElemTable(byte[] data, FileSchema schema)
    {
        var table = schema.ByName("ElemTable");
        var rec = schema.ByName("ElemRec");
        var history = schema.ByName("ElementHistory");
        if (table == null || rec == null || history == null) return null;
        var recFields = FileSchema.SerializedFields(rec);
        var historyFields = FileSchema.SerializedFields(history);
        if (table.Fields.Count == 0 || table.Fields[0].Name != "m_elemArr"
            || table.Fields[0].Ref != rec.Id || (table.Fields[0].Mods >> 4) != 5
            || !recFields.Select(f => f.Name).Order().SequenceEqual(
                new[] { "m_history", "m_id", "m_OwningElementId", "m_partitionId" }.Order())
            || !historyFields.Select(f => f.Name).SequenceEqual(
                new[] { "m_originalElementId", "m_creationDate", "m_lastModificationDate", "m_lastUserModificationDate" })
            || recFields.First(f => f.Name == "m_history").Ref != history.Id
            || schema.FixedSize(rec.Id) is not int length
            || IdWidth(schema) is not int width
            || schema.FieldSize(recFields.First(f => f.Name == "m_OwningElementId")) != width
            || schema.FieldSize(historyFields[0]) != width || schema.FieldSize(historyFields[1]) != 4
            || schema.OffsetOf(rec.Id, "m_history") is not int historyAt
            || schema.OffsetOf(history.Id, "m_creationDate") is not int creationAt
            || schema.OffsetOf(rec.Id, "m_id") is not int idAt
            || schema.OffsetOf(rec.Id, "m_OwningElementId") is not int ownerAt
            || data.Length < 6 || BitConverter.ToUInt16(data, 0) != table.Id)
            return null;
        uint count = BitConverter.ToUInt32(data, 2);
        if (6 + (long)count * length > data.Length) return null;

        var histories = new Dictionary<uint, ElemRecHistory>((int)count);
        var owners = new Dictionary<uint, int>((int)count);
        long previous = -1;
        int skipped = 0;
        for (int i = 0; i < count; i++)
        {
            int at = 6 + i * length;
            ulong id = ReadId(data, at + idAt, width);
            if (previous >= 0 && (long)id <= previous) return null;
            previous = (long)id;
            ulong original = ReadId(data, at + historyAt, width);
            if (!(ElementIdEncoding.IsSupported(unchecked((uint)id)) || id == 0) || id > uint.MaxValue || original > uint.MaxValue)
            {
                skipped++; // an id beyond the 32-bit model id space is not representable
                continue;
            }
            histories[(uint)id] = new ElemRecHistory(
                (uint)original, BitConverter.ToInt32(data, at + historyAt + creationAt));
            long owner = width == 8 ? BitConverter.ToInt64(data, at + ownerAt) : BitConverter.ToInt32(data, at + ownerAt);
            if (owner >= int.MinValue && owner <= int.MaxValue) owners[(uint)id] = (int)owner;
        }
        return new Result(histories, owners, skipped);
    }

    /// <summary>Header category and root class of the elements of the partition streams.</summary>
    internal sealed class Index
    {
        /// <summary>ElementHeader.m_categroryId per element id; the value is null when the id's records disagree or the
        /// value does not fit 32 bits. Ids without a record are absent.</summary>
        internal Dictionary<uint, int?> HeaderCategories { get; } = new();

        /// <summary>ElementHeader.m_designOptionId per element id, with the same agreement rule as <see cref="HeaderCategories"/>.</summary>
        internal Dictionary<uint, int?> HeaderDesignOptions { get; } = new();

        /// <summary>The outline the ElementHeader stores per element id (see <see cref="ElementHeaderCategory.ReadOutline"/>): a verdict when the
        /// records of the id agree it is there or agree there is none, null when they disagree or the structure was not read. Ids of a file whose
        /// schema does not have the header layout the reader relies on are absent.</summary>
        internal Dictionary<uint, ElementHeaderBox?> HeaderBoundingBoxes { get; } = new();

        /// <summary>Class id (of the file's own schema) of the Element-derived block of the id; absent when the id has no
        /// such block or its blocks disagree.</summary>
        internal Dictionary<uint, int> RootClasses { get; } = new();

        internal int Records { get; set; }
        internal int Blocks { get; set; }
    }

    /// <summary>Walks every partition stream (each given as its decoded chunks, which are concatenated) and collects,
    /// for the ids in <paramref name="wanted"/>, the ElementHeader category and the Element-derived block class.
    /// A stream whose entry in <paramref name="framed"/> is set is read by its framing instead: the header records are the
    /// channel-101 records and the blocks the channel-102 records of the project's own block, and the documents of loaded
    /// families are not read. Null when the schema does not declare the expected ElementHeader shape.</summary>
    internal static Index? BuildIndex(
        IReadOnlyList<IReadOnlyList<byte[]>> streams, FileSchema schema, IReadOnlySet<uint> wanted,
        Action<uint, byte[], int, int, int>? onRootBlock = null, IReadOnlyList<PartitionFraming.Framed?>? framed = null)
        => BuildIndexWithPartitions(streams, schema, wanted,
            onRootBlock == null ? null : (id, buf, off, size, cls, _) => onRootBlock(id, buf, off, size, cls), framed);

    /// <summary>Same as <see cref="BuildIndex"/>, passing the framed partition number (or null when unframed) to <paramref name="onRootBlock"/>.</summary>
    internal static Index? BuildIndexWithPartitions(
        IReadOnlyList<IReadOnlyList<byte[]>> streams, FileSchema schema, IReadOnlySet<uint> wanted,
        Action<uint, byte[], int, int, int, int?>? onRootBlock, IReadOnlyList<PartitionFraming.Framed?>? framed = null)
    {
        if (framed != null && framed.Count != streams.Count) throw new ArgumentException("one framing entry per stream", nameof(framed));
        var header = schema.ByName("ElementHeader");
        var element = schema.ByName("Element");
        if (header == null || element == null || header.Fields.Count < 2
            || header.Fields[0].Name != "m_regenHistory" || header.Fields[1].Name != "m_categroryId"
            || schema.FieldSize(header.Fields[1]) is not (4 or 8)
            || IdWidth(schema) is not int width
            || schema.Get(header.Fields[0].Ref) is not { Fields.Count: 1 } regen
            || regen.Fields[0].Name != "m_historyMap" || (regen.Fields[0].Mods >> 4) != 5
            || schema.FixedSize(regen.Fields[0].Ref) is not int entry)
            return null;

        int categoryWidth = schema.FieldSize(header.Fields[1])!.Value;
        var outlineTags = OutlineTags(schema, header, categoryWidth);
        int max = schema.Classes.Keys.Max();
        var isElement = new bool[max + 1];
        foreach (int id in schema.Classes.Keys) isElement[id] = schema.IsSubclassOf(id, element.Id);
        var known = new bool[max + 1];
        foreach (int id in schema.Classes.Keys) known[id] = true;

        var index = new Index();
        var conflictCategory = new HashSet<uint>();
        var conflictDesignOption = new HashSet<uint>();
        var conflictClass = new HashSet<uint>();
        for (int s = 0; s < streams.Count; s++)
        {
            var frame = framed?[s] is { } f && f.IdWidth == width ? f : null;
            var chunks = frame?.Segments ?? streams[s];
            long total = 0;
            foreach (var c in chunks) total += c.Length;
            if (total > int.MaxValue - 64) return null;
            var buf = new byte[total];
            int off = 0;
            foreach (var c in chunks) { Buffer.BlockCopy(c, 0, buf, off, c.Length); off += c.Length; }
            var recordIds = new HashSet<uint>();
            var blockClasses = new Dictionary<uint, int>();   // -1: the stream holds blocks of two classes for the id
            var rootBlocks = onRootBlock == null ? null : new List<(uint Id, int Offset, int Size, int Class)>();
            void Header(int p, int size, uint id)
            {
                index.Records++;
                if (!wanted.Contains(id)) return;
                recordIds.Add(id);
                RecordCategory(buf, p + width - 8, size, categoryWidth, entry, id, index, conflictCategory, conflictDesignOption, outlineTags);
            }
            void Block(int p, int size, uint id, int cls)
            {
                index.Blocks++;
                if (!isElement[cls] || !wanted.Contains(id)) return;
                blockClasses[id] = blockClasses.TryGetValue(id, out int prior) && prior != cls ? -1 : cls;
                rootBlocks?.Add((id, p + width + 8, size, cls));
            }
            if (frame == null) Walk(buf, header.Id, width, known, Header, Block);
            else
                foreach (var item in frame.Items)
                {
                    // an id the 32-bit element table cannot hold is skipped, as the scan skips it
                    if (item.Owner > uint.MaxValue) continue;
                    int p = (int)item.Offset;
                    if (item.Channel == 101 && item.Length >= width + 16 && BitConverter.ToUInt16(buf, p + width + 4) == header.Id)
                        Header(p, item.Length - width - 8, (uint)item.Owner);
                    else if (item.Channel == 102 && item.Length >= width + 20 && BitConverter.ToUInt16(buf, p + width + 8) is var cls && cls < known.Length && known[cls])
                        Block(p, item.Length - width - 12, (uint)item.Owner, cls);
                }
            // payload (class id first) of every Element-derived block whose stream holds the header record of its id
            if (rootBlocks != null)
                foreach (var (blockId, offset, size, cls) in rootBlocks)
                    if (recordIds.Contains(blockId)) onRootBlock!(blockId, buf, offset, size, cls, frame?.Partition);
            foreach (var (id, cls) in blockClasses)
            {
                if (!recordIds.Contains(id)) continue;
                if (cls < 0) conflictClass.Add(id);
                else if (index.RootClasses.TryGetValue(id, out int prior)) { if (prior != cls) conflictClass.Add(id); }
                else index.RootClasses[id] = cls;
            }
        }
        foreach (uint id in conflictCategory) index.HeaderCategories[id] = null;
        foreach (uint id in conflictDesignOption) index.HeaderDesignOptions[id] = null;
        foreach (uint id in conflictClass) index.RootClasses.Remove(id);
        return index;
    }

    /// <summary>The position-by-position scan: every candidate whose length echo holds is a header record or a block (item start, size, id).</summary>
    private static void Walk(byte[] b, int headerId, int w, bool[] known, Action<int, int, uint> header, Action<int, int, uint, int> block)
    {
        int n = b.Length;
        int p = 0;
        while (p + w + 12 <= n)
        {
            uint id = BitConverter.ToUInt32(b, p);
            if (w == 8 && BitConverter.ToUInt32(b, p + 4) != 0) { p++; continue; }

            uint size = BitConverter.ToUInt32(b, p + w);
            if (BitConverter.ToUInt16(b, p + w + 4) == headerId && size >= 8 && size < 4_000_000
                && p + w + 8L + size <= n && BitConverter.ToUInt32(b, p + w + 4 + (int)size) == size)
            {
                header(p, (int)size, id);
                p += w + 8 + (int)size;
                continue;
            }

            uint blockSize = BitConverter.ToUInt32(b, p + w + 4);
            ushort cls = BitConverter.ToUInt16(b, p + w + 8);
            if (blockSize >= 8 && blockSize < 8_000_000 && cls < known.Length && known[cls]
                && p + w + 12L + blockSize <= n && BitConverter.ToUInt32(b, p + w + 8 + (int)blockSize) == blockSize)
            {
                block(p, (int)blockSize, id, cls);
                p += w + 12 + (int)blockSize;
                continue;
            }
            p++;
        }
    }

    /// <summary>
    /// The class tags that m_pBBox and m_parents carry in the records of this file (its own class numbers), or null when the header of the schema
    /// is not the one the outline reader relies on: the ids, m_viewRules, m_abFlags4Bytes and m_classDef in front of the two pointers, in this order,
    /// and the two classes by name.
    /// </summary>
    private static (int Outline, int Parents)? OutlineTags(FileSchema schema, FileSchema.Class header, int idWidth)
    {
        string[] expected =
        [
            "m_regenHistory", "m_categroryId", "m_familyId", "m_ownerViewId", "m_designOptionId", "m_unplacedOwnerId", "m_miscId",
            "m_viewRules", "m_abFlags4Bytes", "m_classDef", "m_pBBox", "m_parents",
        ];
        if (header.Fields.Count != expected.Length || !header.Fields.Select(f => f.Name).SequenceEqual(expected)) return null;
        for (int i = 1; i <= 6; i++)
            if (schema.FieldSize(header.Fields[i]) != idWidth) return null;
        if (header.Fields[8].Type != 4 || header.Fields[10].Type != 0x0E || (header.Fields[10].Mods & 0x0F) == 0
            || header.Fields[11].Type != 0x0E || (header.Fields[11].Mods & 0x0F) == 0)
            return null;
        return schema.ByName("Outline") is { } outline && schema.ByName("ElementParents") is { } parents ? (outline.Id, parents.Id) : null;
    }

    private static void RecordCategory(byte[] b, int p, int size, int categoryWidth, int entry, uint id, Index index, HashSet<uint> conflict,
        HashSet<uint> conflictDesignOption, (int Outline, int Parents)? outlineTags)
    {
        // payload starts at p + 12 with the class id; m_regenHistory: u32 count + count * entry; then m_categroryId (8 bytes)
        uint count = BitConverter.ToUInt32(b, p + 14);
        long categoryAt = p + 18L + count * entry;
        int? value = null;
        if (categoryAt + categoryWidth <= p + 12L + size)
        {
            long raw = categoryWidth == 8 ? BitConverter.ToInt64(b, (int)categoryAt) : BitConverter.ToInt32(b, (int)categoryAt);
            if (raw >= int.MinValue && raw <= int.MaxValue) value = (int)raw;
        }
        if (index.HeaderCategories.TryGetValue(id, out var prior)) { if (prior != value) conflict.Add(id); }
        else index.HeaderCategories[id] = value;
        // m_familyId, m_ownerViewId, m_designOptionId follow the category and have its width (ids)
        long optionAt = categoryAt + 3L * categoryWidth;
        int? option = null;
        if (optionAt + categoryWidth <= p + 12L + size)
        {
            long raw = categoryWidth == 8 ? BitConverter.ToInt64(b, (int)optionAt) : BitConverter.ToInt32(b, (int)optionAt);
            if (raw >= int.MinValue && raw <= int.MaxValue) option = (int)raw;
        }
        if (index.HeaderDesignOptions.TryGetValue(id, out var priorOption)) { if (priorOption != option) conflictDesignOption.Add(id); }
        else index.HeaderDesignOptions[id] = option;
        if (outlineTags is var (outlineTag, parentsTag))
            ElementHeaderCategory.Merge(index.HeaderBoundingBoxes, id,
                categoryAt + categoryWidth <= p + 12L + size
                    ? ElementHeaderCategory.ReadOutline(b, categoryAt, p + 12L + size, categoryWidth, outlineTag, parentsTag) : null);
    }
}
