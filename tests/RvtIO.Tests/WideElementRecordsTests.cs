using System.Text;
using RvtIO.BuiltIns;
using RvtIO.Internal;
using RvtIO.Lookup;
using RvtIO.Model;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

/// <summary>Class schema of the file itself (<see cref="FileSchema"/>) and the 64-bit element table and element records
/// (<see cref="WideElementRecords"/>): hand-made streams, then Test_2025.rvt against the current profile.</summary>
public class WideElementRecordsTests
{

    private sealed class SchemaWriter
    {
        private readonly MemoryStream _s = new();
        public int Next { get; private set; } = 12;

        /// <summary>Appends a top-level class; fields are (name, type, mods, ref).</summary>
        public int Add(string name, int baseId, params (string Name, byte Type, byte Mods, int Ref)[] fields)
        {
            int id = Next++;
            U16(0); U16((ushort)name.Length); Bytes(name);
            U16((ushort)baseId); U32(1); U32((uint)fields.Length);
            foreach (var f in fields)
            {
                U32((uint)f.Name.Length); Bytes(f.Name);
                _s.WriteByte(f.Type); _s.WriteByte(f.Mods); U16(0);
                if (f.Type == 0x0E && (f.Mods & 0x0F) == 0) U16((ushort)f.Ref);
            }
            U32(0);
            return id;
        }

        public byte[] ToArray()
        {
            var bytes = _s.ToArray();
            return [.. bytes, 0, 0, 0, 0, 0, 0, 0, 0];
        }

        private void U16(ushort v) => _s.Write(BitConverter.GetBytes(v));
        private void U32(uint v) => _s.Write(BitConverter.GetBytes(v));
        private void Bytes(string s) => _s.Write(Encoding.ASCII.GetBytes(s));
    }

    private static (string, byte, byte, int) Ref(string name, int cls) => (name, 0x0E, 0, cls);
    private static (string, byte, byte, int) Scalar(string name, byte type) => (name, type, 0, 0);
    private static (string, byte, byte, int) Array(string name, int cls) => (name, 0x0E, 0x50, cls);
    private static (string, byte, byte, int) Pointer(string name) => (name, 0x0E, 0x01, 0);

    private sealed record Ids(int Identifier, int ElementId, int ElemRec, int ElemTable, int Header, int Element, int Wall, int Other, int Level);

    private static (byte[] Stream, Ids Ids) BuildSchema()
    {
        var w = new SchemaWriter();
        int identifier = w.Add("Identifier", 0, Scalar("m_id64", 11));
        int elementId = w.Add("ElementId", 0, Ref("m_id", identifier));
        int original = w.Add("OriginalElementId", 0, Scalar("m_id64", 11));
        int episode = w.Add("EpisodeId", 0, Scalar("m_id", 4));
        int partition = w.Add("PartitionId", 0, Scalar("m_id", 4));
        int history = w.Add("ElementHistory", 0, Ref("m_originalElementId", original), Ref("m_creationDate", episode),
            Ref("m_lastModificationDate", episode), Ref("m_lastUserModificationDate", episode));
        int rec = w.Add("ElemRec", 0, Ref("m_history", history), Ref("m_id", elementId), Ref("m_OwningElementId", elementId),
            Ref("m_partitionId", partition));
        int table = w.Add("ElemTable", 0, Array("m_elemArr", rec));
        int info = w.Add("ElementRegenerationInfo", 0, Ref("m_otherElementId", elementId), Scalar("m_otherInt64", 11),
            Scalar("m_nAtomType", 3));
        int pair = w.Add("std::pair< ElementRegenerationInfo, EpisodeId >", 0, Ref("first", info), Ref("second", episode));
        int regen = w.Add("ElementRegenHistory", 0, Array("m_historyMap", pair));
        int header = w.Add("ElementHeader", 0, Ref("m_regenHistory", regen), Ref("m_categroryId", elementId));
        int element = w.Add("Element", 0);
        int wall = w.Add("Wall", element);
        int other = w.Add("Other", 0);
        int level = w.Add("Level", wall);
        return (w.ToArray(), new Ids(identifier, elementId, rec, table, header, element, wall, other, level));
    }

    [Fact]
    public void FileSchema_ReadsNamesBasesAndFixedSizes()
    {
        var (stream, ids) = BuildSchema();
        var schema = FileSchema.Parse(stream);
        Assert.NotNull(schema);
        Assert.Equal("Wall", schema.Get(ids.Wall)!.Name);
        Assert.Equal(ids.Element, schema.Get(ids.Wall)!.Base);
        Assert.Equal(ids.Header, schema.ByName("ElementHeader")!.Id);
        Assert.True(schema.IsSubclassOf(ids.Level, ids.Element));
        Assert.False(schema.IsSubclassOf(ids.Other, ids.Element));
        Assert.Equal(8, schema.FixedSize(ids.ElementId));
        Assert.Equal(40, schema.FixedSize(ids.ElemRec));
        Assert.Null(schema.FixedSize(ids.ElemTable));   // a container has no fixed size
        Assert.Equal(20, schema.OffsetOf(ids.ElemRec, "m_id"));
    }

    [Fact]
    public void FileSchema_RejectsAnInlineIdThatIsNotTheCounter()
    {
        var (stream, _) = BuildSchema();
        // the base reference of the first class is the 16-bit 0; make it an inline definition with a wrong id
        var broken = (byte[])stream.Clone();
        int baseAt = 2 + 2 + "Identifier".Length;
        broken[baseAt] = 0x20; broken[baseAt + 1] = 0x80;   // inline id 0x20, the counter is 13
        Assert.Null(FileSchema.Parse(broken));
        Assert.Null(FileSchema.Parse([]));
        Assert.Null(FileSchema.Parse(stream[..(stream.Length / 2)]));
    }


    private static byte[] ElemTable(Ids ids, params (ulong Original, int Creation, ulong Id, long Owner)[] rows)
    {
        var s = new MemoryStream();
        s.Write(BitConverter.GetBytes((ushort)ids.ElemTable));
        s.Write(BitConverter.GetBytes((uint)rows.Length));
        foreach (var r in rows)
        {
            s.Write(BitConverter.GetBytes(r.Original));
            s.Write(BitConverter.GetBytes(r.Creation));
            s.Write(BitConverter.GetBytes(0)); s.Write(BitConverter.GetBytes(0));
            s.Write(BitConverter.GetBytes(r.Id));
            s.Write(BitConverter.GetBytes(r.Owner));
            s.Write(BitConverter.GetBytes(0));
        }
        s.Write(new byte[24]);   // fields behind m_elemArr
        return s.ToArray();
    }

    [Fact]
    public void ParseElemTable_ReadsOriginalIdCreationEpisodeAndOwnerOfEveryRow()
    {
        var (stream, ids) = BuildSchema();
        var schema = FileSchema.Parse(stream)!;
        var table = WideElementRecords.ParseElemTable(
            ElemTable(ids, (5, 7, 5, -1), (20, 3, 21, 5), (30, 0, 31, 0x1_0000_0000)), schema);
        Assert.NotNull(table);
        Assert.Equal(new ElemRecHistory(5, 7), table.Histories[5]);
        Assert.Equal(new ElemRecHistory(20, 3), table.Histories[21]);   // the key is m_id, UniqueId uses the original id
        Assert.Equal(-1, table.OwningElementIds[5]);
        Assert.Equal(5, table.OwningElementIds[21]);
        Assert.False(table.OwningElementIds.ContainsKey(31));          // an owner beyond 32 bits is not represented
    }

    [Fact]
    public void ParseElemTable_ReadsTheFourByteIdLayoutOfRevit2023()
    {
        // m_id first, 4-byte ids, 28-byte rows: Identifier.m_id (type 4) instead of m_id64 (type 11)
        var w = new SchemaWriter();
        int identifier = w.Add("Identifier", 0, Scalar("m_id", 4));
        int elementId = w.Add("ElementId", 0, Ref("m_id", identifier));
        int original = w.Add("OriginalElementId", 0, Scalar("m_id", 4));
        int episode = w.Add("EpisodeId", 0, Scalar("m_id", 4));
        int partition = w.Add("PartitionId", 0, Scalar("m_id", 4));
        int history = w.Add("ElementHistory", 0, Ref("m_originalElementId", original), Ref("m_creationDate", episode),
            Ref("m_lastModificationDate", episode), Ref("m_lastUserModificationDate", episode));
        int rec = w.Add("ElemRec", 0, Ref("m_id", elementId), Ref("m_history", history), Ref("m_partitionId", partition),
            Ref("m_OwningElementId", elementId));
        int table = w.Add("ElemTable", 0, Array("m_elemArr", rec));
        var schema = FileSchema.Parse(w.ToArray())!;
        Assert.Equal(4, WideElementRecords.IdWidth(schema));
        Assert.Equal(28, schema.FixedSize(rec));

        var s = new MemoryStream();
        s.Write(BitConverter.GetBytes((ushort)table));
        s.Write(BitConverter.GetBytes(2u));
        foreach (var (id, orig, creation, owner) in new[] { (5, 5, 7, -1), (21, 20, 3, 5) })
            foreach (int v in new[] { id, orig, creation, 0, 0, 9, owner }) s.Write(BitConverter.GetBytes(v));
        s.Write(new byte[24]);
        var wide = WideElementRecords.ParseElemTable(s.ToArray(), schema);
        Assert.NotNull(wide);
        Assert.Equal(new ElemRecHistory(5, 7), wide.Histories[5]);
        Assert.Equal(new ElemRecHistory(20, 3), wide.Histories[21]);
        Assert.Equal(-1, wide.OwningElementIds[5]);
        Assert.Equal(5, wide.OwningElementIds[21]);
    }

    [Fact]
    public void ParseElemTable_KeepsIdZeroSkipsIdsOutsideTheModelIdSpaceAndRejectsBrokenTables()
    {
        var (stream, ids) = BuildSchema();
        var schema = FileSchema.Parse(stream)!;
        var wide = WideElementRecords.ParseElemTable(
            ElemTable(ids, (0, 0, 0, -1), (5, 0, 5, -1), (9, 0, 0x1_0000_0009UL, -1)), schema)!;
        Assert.Equal([0u, 5u], wide.Histories.Keys.ToArray());   // id 0 is a real row (ProjectPhase / DimensionStyle)
        Assert.Equal(1, wide.SkippedRows);
        var good = ElemTable(ids, (5, 0, 5, -1), (6, 0, 6, -1));
        Assert.Null(WideElementRecords.ParseElemTable(ElemTable(ids, (6, 0, 6, -1), (5, 0, 5, -1)), schema));   // not increasing
        Assert.Null(WideElementRecords.ParseElemTable(good[..^30], schema));                                        // truncated
        var wrongClass = (byte[])good.Clone(); wrongClass[0] ^= 1;
        Assert.Null(WideElementRecords.ParseElemTable(wrongClass, schema));
        Assert.Null(WideElementRecords.ParseElemTable(good, FileSchema.Parse(OnlyOther())!));   // schema without the table classes
    }

    private static byte[] OnlyOther()
    {
        var w = new SchemaWriter();
        w.Add("Other", 0);
        return w.ToArray();
    }


    private static byte[] Record(Ids ids, ulong id, int[] regenEpisodes, long category, int tail = 8)
    {
        var p = new MemoryStream();
        p.Write(BitConverter.GetBytes((ushort)ids.Header));
        p.Write(BitConverter.GetBytes((uint)regenEpisodes.Length));
        foreach (int episode in regenEpisodes)
        {
            p.Write(BitConverter.GetBytes(-1L)); p.Write(BitConverter.GetBytes(1L)); p.Write(BitConverter.GetBytes((ushort)3));
            p.Write(BitConverter.GetBytes(episode));
        }
        p.Write(BitConverter.GetBytes(category));
        p.Write(new byte[tail]);
        return Frame(id, null, p.ToArray());
    }

    private static byte[] Block(ulong id, int classId, int payloadLength = 40)
    {
        var p = new byte[payloadLength];
        BitConverter.GetBytes((ushort)classId).CopyTo(p, 0);
        return Frame(id, 0xDEADBEEFu, p);
    }

    /// <summary>A record has no word between the id and the size; a block has one.</summary>
    private static byte[] Frame(ulong id, uint? word, byte[] payload)
    {
        var s = new MemoryStream();
        s.Write(BitConverter.GetBytes(id));
        if (word is uint w) s.Write(BitConverter.GetBytes(w));
        s.Write(BitConverter.GetBytes((uint)payload.Length));
        s.Write(payload);
        s.Write(BitConverter.GetBytes((uint)payload.Length));
        return s.ToArray();
    }

    private static byte[] Join(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    [Fact]
    public void BuildIndex_ReadsHeaderCategoryAndTheElementDerivedBlockClass()
    {
        var (stream, ids) = BuildSchema();
        var schema = FileSchema.Parse(stream)!;
        var junk = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 };
        var data = Join(
            Record(ids, 10, [], -2000011),
            Block(10, ids.Level),
            junk,
            Record(ids, 11, [4, 9], -1),                 // two regeneration entries before the category
            Block(11, ids.Other),                        // not an Element class: no root class
            Block(11, ids.Wall),
            Record(ids, 99, [], 5),                      // id not wanted
            Record(ids, 12, [], 0x1_0000_0000));         // a category beyond 32 bits is unknown
        var index = WideElementRecords.BuildIndex([[data[..30], data[30..]]], schema, new HashSet<uint> { 10, 11, 12, 13 })!;
        Assert.Equal(-2000011, index.HeaderCategories[10]);
        Assert.Equal(-1, index.HeaderCategories[11]);
        Assert.Null(index.HeaderCategories[12]);
        Assert.False(index.HeaderCategories.ContainsKey(99));
        Assert.False(index.HeaderCategories.ContainsKey(13));
        Assert.Equal(ids.Level, index.RootClasses[10]);
        Assert.Equal(ids.Wall, index.RootClasses[11]);
        Assert.False(index.RootClasses.ContainsKey(12));
        Assert.Equal(4, index.Records);
    }

    [Fact]
    public void BuildIndex_ReadsTheRecordAndBlockOfIdZeroAndIgnoresAZeroRun()
    {
        var (stream, ids) = BuildSchema();
        var schema = FileSchema.Parse(stream)!;
        var data = Join(
            Record(ids, 0, [], -2000112),
            Block(0, ids.Level),
            new byte[24],                                // a run of zeros is no item (size 0)
            Record(ids, 1, [], -1));
        var index = WideElementRecords.BuildIndex([[data]], schema, new HashSet<uint> { 0, 1 })!;
        Assert.Equal(-2000112, index.HeaderCategories[0]);
        Assert.Equal(ids.Level, index.RootClasses[0]);
        Assert.Equal(-1, index.HeaderCategories[1]);
        Assert.Equal(2, index.Records);
        Assert.DoesNotContain(2u, index.HeaderCategories.Keys);
        var without = WideElementRecords.BuildIndex([[data]], schema, new HashSet<uint> { 1 })!;
        Assert.False(without.HeaderCategories.ContainsKey(0));   // only wanted ids are collected
    }

    [Fact]
    public void BuildIndex_DropsAValueTheCopiesOfAnIdDisagreeOn()
    {
        var (stream, ids) = BuildSchema();
        var schema = FileSchema.Parse(stream)!;
        var copyA = Join(Record(ids, 10, [], -2000011), Block(10, ids.Level), Record(ids, 11, [], -5), Block(11, ids.Wall));
        var copyB = Join(Record(ids, 10, [], -2000011), Block(10, ids.Level), Record(ids, 11, [], -6), Block(11, ids.Level));
        var index = WideElementRecords.BuildIndex([[copyA], [copyB]], schema, new HashSet<uint> { 10, 11 })!;
        Assert.Equal(-2000011, index.HeaderCategories[10]);   // two copies that agree stay known
        Assert.Equal(ids.Level, index.RootClasses[10]);
        Assert.Null(index.HeaderCategories[11]);
        Assert.False(index.RootClasses.ContainsKey(11));
    }

    [Fact]
    public void BuildIndex_IgnoresAnElementBlockInAStreamWithoutTheHeaderRecordOfTheId()
    {
        var (stream, ids) = BuildSchema();
        var schema = FileSchema.Parse(stream)!;
        var project = Join(Record(ids, 10, [], -2000011), Block(10, ids.Level));
        var foreign = Join(Record(ids, 11, [], -1), Block(10, ids.Wall), Block(11, ids.Wall));
        var index = WideElementRecords.BuildIndex([[project], [foreign]], schema, new HashSet<uint> { 10, 11 })!;
        Assert.Equal(ids.Level, index.RootClasses[10]);   // the Wall block of the second stream is not the root of 10
        Assert.Equal(ids.Wall, index.RootClasses[11]);
    }

    [Fact]
    public void BuildIndex_ReadsAFramedStreamByTheRecordsOfTheProjectBlockOnly()
    {
        var (stream, ids) = BuildSchema();
        var schema = FileSchema.Parse(stream)!;
        byte[] header = Record(ids, 10, [], -2000011), root = Block(10, ids.Level);
        // the same id in the document of a loaded family, a keyed block the framing does not list
        var data = Join(header, root, Record(ids, 10, [], -5), Block(10, ids.Wall));
        var framed = new PartitionFraming.Framed([data[..20], data[20..]],
            [new(101, 10, 0, header.Length), new(102, 10, header.Length, root.Length)], 8);
        var roots = new List<(uint Id, int Offset, int Size, int Class)>();

        var index = WideElementRecords.BuildIndex([[data]], schema, new HashSet<uint> { 10 }, (id, _, offset, size, cls) => roots.Add((id, offset, size, cls)), [framed])!;

        Assert.Equal(-2000011, index.HeaderCategories[10]);
        Assert.Equal(ids.Level, index.RootClasses[10]);
        Assert.Equal((1, 1), (index.Records, index.Blocks));
        Assert.Equal([(10u, header.Length + 16, root.Length - 20, ids.Level)], roots);
        // the scan takes the family's records for a second copy of the element, and the copies disagree
        var scanned = WideElementRecords.BuildIndex([[data]], schema, new HashSet<uint> { 10 })!;
        Assert.Null(scanned.HeaderCategories[10]);
        Assert.False(scanned.RootClasses.ContainsKey(10));
        // a framing read with another id width is not used: the stream is scanned
        var otherWidth = WideElementRecords.BuildIndex([[data]], schema, new HashSet<uint> { 10 }, null, [framed with { IdWidth = 4 }])!;
        Assert.Null(otherWidth.HeaderCategories[10]);
    }

    private sealed record OutlineIds(int Header, int Outline, int Parents, int Level);

    /// <summary>A schema whose ElementHeader has the layout of the files: ids, m_viewRules, m_abFlags4Bytes, m_classDef and the pointers
    /// m_pBBox and m_parents. The class numbers are those of this file, not those of the Revit 2022 table.</summary>
    private static (byte[] Stream, OutlineIds Ids) BuildOutlineSchema(bool withParents = true)
    {
        var w = new SchemaWriter();
        int identifier = w.Add("Identifier", 0, Scalar("m_id64", 11));
        int elementId = w.Add("ElementId", 0, Ref("m_id", identifier));
        int episode = w.Add("EpisodeId", 0, Scalar("m_id", 4));
        int info = w.Add("ElementRegenerationInfo", 0, Ref("m_otherElementId", elementId), Scalar("m_otherInt64", 11), Scalar("m_nAtomType", 3));
        int pair = w.Add("std::pair< ElementRegenerationInfo, EpisodeId >", 0, Ref("first", info), Ref("second", episode));
        int regen = w.Add("ElementRegenHistory", 0, Array("m_historyMap", pair));
        w.Add("ElemRec", 0, Ref("m_id", elementId));
        int viewRules = w.Add("ViewRules", 0, Scalar("m_bits", 4));
        int classDef = w.Add("ClassDefinitionRef", 0, Scalar("m_ref", 3));
        int outline = w.Add("Outline", 0, Scalar("m_min", 11));
        int parents = w.Add("ElementParents", 0, Scalar("m_count", 4));
        int header = w.Add("ElementHeader", 0, Ref("m_regenHistory", regen), Ref("m_categroryId", elementId), Ref("m_familyId", elementId),
            Ref("m_ownerViewId", elementId), Ref("m_designOptionId", elementId), Ref("m_unplacedOwnerId", elementId), Ref("m_miscId", elementId),
            Ref("m_viewRules", viewRules), Scalar("m_abFlags4Bytes", 4), Ref("m_classDef", classDef),
            Pointer("m_pBBox"), Pointer(withParents ? "m_parents" : "m_other"));
        int element = w.Add("Element", 0);
        int level = w.Add("Level", element);
        return (w.ToArray(), new OutlineIds(header, outline, parents, level));
    }

    /// <summary>A header record of the 64-bit layout whose outline is <paramref name="box"/> (none when null), tagged with the classes
    /// of the schema it is written for.</summary>
    private static byte[] OutlineRecord(OutlineIds ids, ulong id, double[]? box, int regenEntries = 0, int? outlineTag = null)
    {
        var p = new MemoryStream();
        p.Write(BitConverter.GetBytes((ushort)ids.Header));
        p.Write(BitConverter.GetBytes((uint)regenEntries));
        for (int i = 0; i < regenEntries; i++)
        {
            // an entry whose bytes look like an absent outline in front of m_parents
            p.Write(new byte[] { 0, 0, 0xFF, 0xFF, 0xFF, 0xFF, (byte)ids.Parents, (byte)(ids.Parents >> 8) });
            p.Write(new byte[22 - 8]);
        }
        p.Write(BitConverter.GetBytes(-2000011L));            // m_categroryId
        for (int i = 0; i < 5; i++) p.Write(BitConverter.GetBytes(-1L));
        p.Write(BitConverter.GetBytes(0xFFFF8000u));          // m_viewRules
        p.Write(BitConverter.GetBytes(0x000109B8u));          // m_abFlags4Bytes
        p.Write(BitConverter.GetBytes((ushort)0x0725));       // m_classDef
        if (box == null) p.Write(BitConverter.GetBytes(0u));
        else
        {
            p.Write(BitConverter.GetBytes(0xFFFFFFFFu));
            p.Write(BitConverter.GetBytes((ushort)(outlineTag ?? ids.Outline)));
        }
        p.Write(BitConverter.GetBytes(0xFFFFFFFFu));
        p.Write(BitConverter.GetBytes((ushort)ids.Parents));
        foreach (double value in box ?? []) p.Write(BitConverter.GetBytes(value));
        return Frame(id, null, p.ToArray());
    }

    [Fact]
    public void BuildIndex_ReadsTheOutlineOfTheHeaderWithTheClassNumbersOfTheFile()
    {
        var (stream, ids) = BuildOutlineSchema();
        var schema = FileSchema.Parse(stream)!;
        Assert.NotEqual(297, ids.Outline);
        double[] box = [-1.5, -2.25, 0.0, 3.75, 4.5, 12.0];
        var data = Join(
            OutlineRecord(ids, 10, box),
            OutlineRecord(ids, 11, null),
            OutlineRecord(ids, 12, box, regenEntries: 2),            // the entries hold the bytes of an absent outline
            OutlineRecord(ids, 13, box, outlineTag: ids.Outline + 1), // another class behind m_pBBox: not the structure
            OutlineRecord(ids, 14, [3, 3, 3, 0, 0, 0]),              // not a box: a counter must not be taken for one
            OutlineRecord(ids, 99, box));                             // not wanted
        var index = WideElementRecords.BuildIndex([[data]], schema, new HashSet<uint> { 10, 11, 12, 13, 14, 15 })!;

        var present = Assert.IsType<ElementHeaderBox>(index.HeaderBoundingBoxes[10]);
        Assert.True(present.Present);
        Assert.Equal(box, present.Box);
        Assert.False(Assert.IsType<ElementHeaderBox>(index.HeaderBoundingBoxes[11]).Present);
        Assert.Equal(box, index.HeaderBoundingBoxes[12]!.Box);
        Assert.Null(index.HeaderBoundingBoxes[13]);
        Assert.Null(index.HeaderBoundingBoxes[14]);
        Assert.False(index.HeaderBoundingBoxes.ContainsKey(15));
        Assert.False(index.HeaderBoundingBoxes.ContainsKey(99));
        Assert.Equal(-2000011, index.HeaderCategories[10]);   // the category and the outline are read from the same record
    }

    [Fact]
    public void BuildIndex_DropsAnOutlineTheCopiesOfAnIdDisagreeOn()
    {
        var (stream, ids) = BuildOutlineSchema();
        var schema = FileSchema.Parse(stream)!;
        double[] box = [0, 0, 0, 1, 1, 1];
        var copyA = Join(OutlineRecord(ids, 10, box), OutlineRecord(ids, 11, box), OutlineRecord(ids, 12, null));
        var copyB = Join(OutlineRecord(ids, 10, box), OutlineRecord(ids, 11, null), OutlineRecord(ids, 12, null));
        var index = WideElementRecords.BuildIndex([[copyA], [copyB]], schema, new HashSet<uint> { 10, 11, 12 })!;
        Assert.Equal(box, index.HeaderBoundingBoxes[10]!.Box);   // two copies that agree stay known
        Assert.Null(index.HeaderBoundingBoxes[11]);              // a box and none: unknown
        Assert.False(index.HeaderBoundingBoxes[12]!.Present);
    }

    [Fact]
    public void BuildIndex_ReadsNoOutlineWhereTheSchemaDoesNotHaveTheHeaderLayout()
    {
        // a header of only the history and the category (the schema of the other tests), and one whose last field is not m_parents
        var (stream, ids) = BuildSchema();
        var data = Join(Record(ids, 10, [], -2000011));
        var shortHeader = WideElementRecords.BuildIndex([[data]], FileSchema.Parse(stream)!, new HashSet<uint> { 10 })!;
        Assert.Equal(-2000011, shortHeader.HeaderCategories[10]);
        Assert.Empty(shortHeader.HeaderBoundingBoxes);

        var (other, outlineIds) = BuildOutlineSchema(withParents: false);
        var index = WideElementRecords.BuildIndex([[OutlineRecord(outlineIds, 10, [0, 0, 0, 1, 1, 1])]], FileSchema.Parse(other)!, new HashSet<uint> { 10 })!;
        Assert.Equal(-2000011, index.HeaderCategories[10]);
        Assert.Empty(index.HeaderBoundingBoxes);
    }

    [Fact]
    public void BuildIndex_RejectsASizeWhoseFooterDoesNotEchoIt()
    {
        var (stream, ids) = BuildSchema();
        var schema = FileSchema.Parse(stream)!;
        var record = Record(ids, 10, [], -7);
        record[^1] ^= 1;
        var index = WideElementRecords.BuildIndex([[record]], schema, new HashSet<uint> { 10 })!;
        Assert.Empty(index.HeaderCategories);
        Assert.Equal(0, index.Records);
    }


    /// <summary>One element per (class, header category) pair of Test_2025.rvt: id, class, unique id, header category.
    /// The whole table agrees: 8,342 of 8,342 elements for each of the three values.</summary>
    private static readonly (int Id, string Class, string UniqueId, int Category)[] MeshVectors =
    [
        (1, "AllProjectPhases", "e3e052f9-0156-11d5-9301-0000863f27ad-00000001", -1),
        (2, "PenWidthTableElem", "e3e052f9-0156-11d5-9301-0000863f27ad-00000002", -1),
        (3, "FillPatternElem", "e3e052f9-0156-11d5-9301-0000863f27ad-00000003", -1),
        (10, "LinePatternElem", "e3e052f9-0156-11d5-9301-0000863f27ad-0000000a", -1),
        (23, "MaterialElem", "e3e052f9-0156-11d5-9301-0000863f27ad-00000017", -2000700),
        (30, "GStyleElem", "e3e052f9-0156-11d5-9301-0000863f27ad-0000001e", -1),
        (230, "DBViewProject", "e3e052f9-0156-11d5-9301-0000863f27ad-000000e6", -1),
        (231, "Viewer", "e3e052f9-0156-11d5-9301-0000863f27ad-000000e7", -1),
        (232, "Viewport", "c3f5348f-6947-4ddf-aa1e-749882f86acc-000000e8", -2000510),
        (233, "DBDrawing", "e3e052f9-0156-11d5-9301-0000863f27ad-000000e9", -1),
        (234, "LeaderStyle", "e3e052f9-0156-11d5-9301-0000863f27ad-000000ea", -1),
        (235, "CategoryElem", "e3e052f9-0156-11d5-9301-0000863f27ad-000000eb", -1),
        (277, "DimensionStyle", "e3e052f9-0156-11d5-9301-0000863f27ad-00000115", -1),
        (280, "FontElem", "e3e052f9-0156-11d5-9301-0000863f27ad-00000118", -1),
        (299, "ColorFillSymbol", "e3e052f9-0156-11d5-9301-0000863f27ad-0000012b", -2000550),
        (305, "LevelAttributes", "e3e052f9-0156-11d5-9301-0000863f27ad-00000131", -2000240),
        (311, "Level", "e3e052f9-0156-11d5-9301-0000863f27ad-00000137", -2000240),
        (312, "DBViewPlan", "c3f5348f-6947-4ddf-aa1e-749882f86acc-00000138", -2000279),
        (324, "ViewportAttributes", "e3e052f9-0156-11d5-9301-0000863f27ad-00000144", -1),
        (338, "CurtainRoofAttributes", "e3e052f9-0156-11d5-9301-0000863f27ad-00000152", -2000035),
        (339, "FloorAttributes", "e3e052f9-0156-11d5-9301-0000863f27ad-00000153", -2000032),
        (341, "GridAttributes", "e3e052f9-0156-11d5-9301-0000863f27ad-00000155", -2000220),
        (350, "RampAttributes", "e3e052f9-0156-11d5-9301-0000863f27ad-0000015e", -2000180),
        (357, "InteriorElevAttributes", "e3e052f9-0156-11d5-9301-0000863f27ad-00000165", -1),
        (361, "TextNoteAttributes", "e3e052f9-0156-11d5-9301-0000863f27ad-00000169", -1),
        (365, "TagNoteAttributes", "e3e052f9-0156-11d5-9301-0000863f27ad-0000016d", -1),
        (371, "PhaseFilterElem", "e3e052f9-0156-11d5-9301-0000863f27ad-00000173", -1),
        (397, "BasicWallType", "e3e052f9-0156-11d5-9301-0000863f27ad-0000018d", -2000011),
        (757, "CompoundCeilingType", "458c0f0a-01bb-11d5-9302-0000863f27ad-000002f5", -2000038),
        (1049, "FamilySymbol", "458c1092-01bb-11d5-9302-0000863f27ad-00000419", -2000400),
        (1141, "FamilySymbol", "458c1092-01bb-11d5-9302-0000863f27ad-00000475", -2000538),
        (3325, "SketchPlane", "54cd0060-067b-11d5-9309-0000863f27ad-00000cfd", -1),
        (4169, "FamilySymbol", "fca46435-22e5-11d5-932d-0000863f27ad-00001049", -2000515),
        (7843, "RevealAttr", "2d99307a-66cd-4439-b173-5c88d252416e-00001ea3", -2000182),
        (7844, "CorniceAttr", "2d99307a-66cd-4439-b173-5c88d252416e-00001ea4", -2000181),
        (7848, "Text3dAttrSymbol", "2d99307a-66cd-4439-b173-5c88d252416e-00001ea8", -2000151),
        (7850, "TrueNorth", "2d99307a-66cd-4439-b173-5c88d252416e-00001eaa", -1),
        (8142, "InteriorElev", "f9c62406-7c50-11d5-93b8-0000863f27ad-00001fce", -2000535),
        (8152, "InteriorElevArrow", "f9c62406-7c50-11d5-93b8-0000863f27ad-00001fd8", -2000278),
        (8157, "DBViewSection", "f9c62406-7c50-11d5-93b8-0000863f27ad-00001fdd", -2000279),
        (8482, "Family", "2d99307a-66cd-4439-b173-5c88d252416e-00002122", -1),
        (8484, "FamilySurrogate", "2d99307a-66cd-4439-b173-5c88d252416e-00002124", -1),
        (8953, "ContourSpacingElem", "2d99307a-66cd-4439-b173-5c88d252416e-000022f9", -2001342),
        (9481, "ExtentElem", "2d99307a-66cd-4439-b173-5c88d252416e-00002509", -1),
        (9490, "AreaMeasureElem", "2d99307a-66cd-4439-b173-5c88d252416e-00002512", -2003201),
        (9492, "AreaTypeElem", "2d99307a-66cd-4439-b173-5c88d252416e-00002514", -1),
        (9744, "AreaSchemePlanTopologies", "2d99307a-66cd-4439-b173-5c88d252416e-00002610", -1),
        (12344, "FamilySymbol", "6c4c5a1e-a051-47b9-b462-2ab655b01df8-00003038", -2000127),
        (12589, "ProjectPhase", "7175a733-90ff-4e0d-a3e8-3878474a97e7-0000312d", -2000112),
        (12799, "DaylightSourceIdSet", "2d99307a-66cd-4439-b173-5c88d252416e-000031ff", -1),
        (16209, "ParamElemFamily", "8ea7ba6d-b5c0-4453-9a12-cbe742c08a48-00003f51", -1),
        (16216, "FamilySymbol", "8ea7ba6d-b5c0-4453-9a12-cbe742c08a48-00003f58", -2003000),
        (19236, "UnitsElem", "2d99307a-66cd-4439-b173-5c88d252416e-00004b24", -1),
        (19878, "RoofSoffitType", "e8e255a3-8f4e-49b9-8324-0178422c4a87-00004da6", -2001393),
        (19885, "FasciaAttr", "e8e255a3-8f4e-49b9-8324-0178422c4a87-00004dad", -2001390),
        (19886, "GutterAttr", "e8e255a3-8f4e-49b9-8324-0178422c4a87-00004dae", -2001391),
        (19887, "EdgeSlabAttr", "e8e255a3-8f4e-49b9-8324-0178422c4a87-00004daf", -2001392),
        (19895, "PrintSettings", "61fcdeab-598f-4238-9495-16451e4c8e3b-00004db7", -1),
        (19902, "ContourLabelingAttributes", "61fcdeab-598f-4238-9495-16451e4c8e3b-00004dbe", -1),
        (21308, "ParamElemExternal", "0eddca8c-7a33-4302-8e36-2334fb464e07-0000533c", -1),
        (21437, "SpotElevationStyle", "787d060b-613e-4fae-8a3b-375c41223f73-000053bd", -1),
        (21747, "GeoSite", "925046e6-d8f7-4064-bf7b-35b3257e1d76-000054f3", -2000111),
        (21748, "GeoLocation", "925046e6-d8f7-4064-bf7b-35b3257e1d76-000054f4", -2000110),
        (23738, "FamilySymbol", "c881ef12-8bcc-40e0-b9a7-66b44a62b6b0-00005cba", -2005100),
        (23741, "ViewReferencing", "c3f5348f-6947-4ddf-aa1e-749882f86acc-00005cbd", -1),
        (23742, "PropertyAttr", "c3f5348f-6947-4ddf-aa1e-749882f86acc-00005cbe", -2001265),
        (24584, "FamilySymbol", "78f7f91b-ffb7-49fd-8414-2bd250ee70c9-00006008", -2006040),
        (24867, "FamilySymbol", "78f7f91b-ffb7-49fd-8414-2bd250ee70c9-00006123", -2006020),
        (24927, "SketchGrid", "c3f5348f-6947-4ddf-aa1e-749882f86acc-0000615f", -2000055),
        (24945, "BrowserOrganization", "c3f5348f-6947-4ddf-aa1e-749882f86acc-00006171", -1),
        (26009, "AllPlanTopologies", "c3f5348f-6947-4ddf-aa1e-749882f86acc-00006599", -1),
        (26010, "ParamElemProject", "0146e0f4-e0cd-4b0c-891f-04555e61e4ac-0000659a", -1),
        (26011, "ParamBinding", "0146e0f4-e0cd-4b0c-891f-04555e61e4ac-0000659b", -1),
        (26029, "SectionAttributes", "c3f5348f-6947-4ddf-aa1e-749882f86acc-000065ad", -1),
        (28598, "StairsAttributes", "c9b18df7-1ca6-432a-95d4-4f51a23b84f0-00006fb6", -2000120),
        (29109, "RepeatingDetailAttributes", "009de6df-1eda-42c3-b66f-c5cf297bce54-000071b5", -2002000),
        (29292, "FamilySymbol", "009de6df-1eda-42c3-b66f-c5cf297bce54-0000726c", -2002000),
        (46017, "ExternalParamLock", "b21880ac-7066-44bf-b40f-f58cc6988ce6-0000b3c1", -1),
        (49026, "JoistSystemAttr", "281263d6-c0be-4b93-a3e6-255e17a505c0-0000bf82", -2001327),
        (49029, "RevisionCloudAttr", "281263d6-c0be-4b93-a3e6-255e17a505c0-0000bf85", -2006060),
        (49030, "ProjectRevision", "281263d6-c0be-4b93-a3e6-255e17a505c0-0000bf86", -2006070),
        (49031, "AllProjectRevisions", "281263d6-c0be-4b93-a3e6-255e17a505c0-0000bf87", -1),
        (49034, "CurtaSystemAttr", "281263d6-c0be-4b93-a3e6-255e17a505c0-0000bf8a", -2000340),
        (49504, "ProjectInfo", "8764c510-57b7-44c3-bddf-266d86c26380-0000c160", -2003101),
        (49508, "CalloutTag", "8764c510-57b7-44c3-bddf-266d86c26380-0000c164", -1),
        (49552, "DBViewType", "8764c510-57b7-44c3-bddf-266d86c26380-0000c190", -1),
        (51438, "LoadNatureElem", "d8c55e1e-6a1d-4cfd-b137-7536d455a562-0000c8ee", -1),
        (51439, "LoadCaseElem", "d8c55e1e-6a1d-4cfd-b137-7536d455a562-0000c8ef", -2005210),
        (54520, "SunAndShadowSettings", "b8feb22d-9c38-47b2-9356-5f4b0ef0758a-0000d4f8", -2009609),
        (54523, "ModelGraphicsStyle", "b8feb22d-9c38-47b2-9356-5f4b0ef0758a-0000d4fb", -1),
        (54537, "StackedWallType", "4d24d0d6-38f1-47b8-a376-b40c6432dad5-0000d509", -2003500),
        (54539, "SysPanelFamSym", "4d24d0d6-38f1-47b8-a376-b40c6432dad5-0000d50b", -2000170),
        (54663, "StructSettingsElem", "ecb49c0e-fa94-4650-8e27-46fd03cd7b57-0000d587", -1),
        (55171, "RbsWireType", "eb0726c0-8df6-4f21-bf23-9b9f8b698fae-0000d783", -2008039),
        (55359, "RbsVoltageType", "d6ee40b0-dec2-46d0-ac9d-a538418bbdd3-0000d83f", -2008040),
        (55360, "RbsDistributionSysType", "d6ee40b0-dec2-46d0-ac9d-a538418bbdd3-0000d840", -2008041),
        (69707, "AreaReportSettingsElem", "d184bba2-3f08-48f0-b66f-ed4b94e431c0-0001104b", -1),
        (69851, "RbsDbViewSystemNavigator", "ae2fa8e5-0195-4f7a-b1fa-be87416d34f4-000110db", -1),
        (85092, "AreaSettingsElem", "cb2b876f-82de-42da-83a5-73301335db15-00014c64", -1),
        (85104, "ReactionsUpToDateElem", "cb2b876f-82de-42da-83a5-73301335db15-00014c70", -1),
        (85113, "ContFootingType", "c0cc0b67-41ac-46d7-bc80-e9943b96122f-00014c79", -2001300),
        (86143, "FilledRegionAttributes", "fea3c4bf-e801-4aeb-a788-a17fbcee79a5-0001507f", -2002000),
        (86204, "CoordinateSystemDisplayElem", "7f70fb92-8b7f-42e5-9898-4667e717d385-000150bc", -2000977),
        (86291, "KeynoteTable", "7f70fb92-8b7f-42e5-9898-4667e717d385-00015113", -1),
        (86313, "FloorAttributes", "5adccf95-7ef8-4f40-aa7c-dd3dcfd6d307-00015129", -2001300),
        (86315, "KeynotingSystem", "5adccf95-7ef8-4f40-aa7c-dd3dcfd6d307-0001512b", -1),
        (99859, "ColorFillSchema", "d227b3d4-3a43-463c-bd6e-c15a03ddc915-00018613", -2000552),
        (99880, "CoverType", "d227b3d4-3a43-463c-bd6e-c15a03ddc915-00018628", -2009014),
        (99910, "ReferenceViewerAttributes", "d227b3d4-3a43-463c-bd6e-c15a03ddc915-00018646", -1),
        (99928, "ConstructionSetProject", "d227b3d4-3a43-463c-bd6e-c15a03ddc915-00018658", -1),
        (100162, "FamilySymbol", "5beb3c18-5531-4220-bce0-15a136c3cf67-00018742", -2000197),
        (102105, "WallJoinDefaultSetting", "3eac4dbf-eafd-4417-af61-3e64b2f4fe60-00018ed9", -1),
        (102126, "RbsDuctSizesElem", "3eac4dbf-eafd-4417-af61-3e64b2f4fe60-00018eee", -1),
        (102127, "RbsDuctSettingsElem", "3eac4dbf-eafd-4417-af61-3e64b2f4fe60-00018eef", -1),
        (102128, "RbsPipeSettingsElem", "3eac4dbf-eafd-4417-af61-3e64b2f4fe60-00018ef0", -1),
        (102129, "RbsWireSettingsElem", "3eac4dbf-eafd-4417-af61-3e64b2f4fe60-00018ef1", -1),
        (102356, "RbsPipeMaterialType", "de0d18ae-d6da-4e8e-8196-33bb609646bc-00018fd4", -2008104),
        (102357, "RbsPipeConnectionType", "de0d18ae-d6da-4e8e-8196-33bb609646bc-00018fd5", -2008103),
        (102360, "RbsPipeScheduleType", "de0d18ae-d6da-4e8e-8196-33bb609646bc-00018fd8", -2008105),
        (102379, "RbsPipeSizesElem", "de0d18ae-d6da-4e8e-8196-33bb609646bc-00018feb", -1),
        (102380, "RbsFluidType", "de0d18ae-d6da-4e8e-8196-33bb609646bc-00018fec", -2008106),
        (102410, "RbsWireSizesElem", "de0d18ae-d6da-4e8e-8196-33bb609646bc-0001900a", -1),
        (102411, "RbsWireMaterialType", "de0d18ae-d6da-4e8e-8196-33bb609646bc-0001900b", -2008111),
        (102415, "RbsWireInsulationType", "de0d18ae-d6da-4e8e-8196-33bb609646bc-0001900f", -2008112),
        (102441, "RbsWireTemperatureRatingType", "de0d18ae-d6da-4e8e-8196-33bb609646bc-00019029", -2008113),
        (102444, "Dwg2dExportUserSettingsData", "de0d18ae-d6da-4e8e-8196-33bb609646bc-0001902c", -1),
        (102461, "ZoneScheme", "de0d18ae-d6da-4e8e-8196-33bb609646bc-0001903d", -1),
        (102462, "ZoneElement", "de0d18ae-d6da-4e8e-8196-33bb609646bc-0001903e", -2008107),
        (102467, "AutoJoinTracker", "de0d18ae-d6da-4e8e-8196-33bb609646bc-00019043", -1),
        (102842, "AutoCamSettingsElem", "178ce0fa-d977-4edf-83db-636178e623fb-000191ba", -1),
        (103632, "GCSTracker", "1d0aa0f6-39f7-481f-afaa-d7c0b72c18ad-000194d0", -1),
        (105532, "HVACLoadScheduleElem", "307239b9-bc24-4011-89fc-8995bb8a8eaf-00019c3c", -2008121),
        (105557, "HVACLoadBuildingTypeElem", "307239b9-bc24-4011-89fc-8995bb8a8eaf-00019c55", -2008120),
        (105590, "HVACLoadSpaceTypeElem", "307239b9-bc24-4011-89fc-8995bb8a8eaf-00019c76", -2008119),
        (105779, "HalftoneUnderlaySettings", "ff5057b3-20f2-4623-9feb-99c9a41823e7-00019d33", -1),
        (111426, "BasePoint", "99c3185a-14ad-4fd5-8a9f-b55682632dcb-0001b342", -2001272),
        (111429, "BasePoint", "99c3185a-14ad-4fd5-8a9f-b55682632dcb-0001b345", -2001271),
        (112390, "TilePatternType", "e22a2abf-9b9e-4cbb-a56c-cb2c81f03b55-0001b706", -2003331),
        (113109, "ActiveGeoLocationTrackingElement", "4548e219-5383-431f-8813-bdd530cb5fce-0001b9d5", -1),
        (113122, "StructConnectionType", "f0b6bddb-eb31-4b4b-b037-478fa3ad24da-0001b9e2", -1),
        (113134, "WorksetVisibilitySettingsElem", "acdcb57d-9db0-47d7-9343-f8bbd09211f8-0001b9ee", -1),
        (113159, "ElectricalDemandFactorDefinition", "acdcb57d-9db0-47d7-9343-f8bbd09211f8-0001ba07", -2008142),
        (113160, "ElectricalLoadClassification", "acdcb57d-9db0-47d7-9343-f8bbd09211f8-0001ba08", -2008143),
        (113164, "ConduitStandardType", "acdcb57d-9db0-47d7-9343-f8bbd09211f8-0001ba0c", -2008144),
        (113168, "ConduitSizesElem", "acdcb57d-9db0-47d7-9343-f8bbd09211f8-0001ba10", -1),
        (113169, "CableTraySizesElem", "acdcb57d-9db0-47d7-9343-f8bbd09211f8-0001ba11", -1),
        (113173, "CableTraySettingsElem", "acdcb57d-9db0-47d7-9343-f8bbd09211f8-0001ba15", -1),
        (113174, "ConduitSettingsElem", "acdcb57d-9db0-47d7-9343-f8bbd09211f8-0001ba16", -1),
        (115987, "ProjectCopySettingsElem", "3fd56013-b5d2-4097-9cfb-42ee8d6381e5-0001c513", -1),
        (115988, "EnergyDataSettings", "3fd56013-b5d2-4097-9cfb-42ee8d6381e5-0001c514", -1),
        (116082, "PanelScheduleTemplate", "3fd56013-b5d2-4097-9cfb-42ee8d6381e5-0001c572", -2008145),
        (116083, "PanelScheduleTemplate", "3fd56013-b5d2-4097-9cfb-42ee8d6381e5-0001c573", -2008147),
        (116084, "PanelScheduleTemplate", "3fd56013-b5d2-4097-9cfb-42ee8d6381e5-0001c574", -2008146),
        (116099, "ParameterFilterElement", "3fd56013-b5d2-4097-9cfb-42ee8d6381e5-0001c583", -1),
        (116120, "ReconcileBrowserSettingsElem", "3fd56013-b5d2-4097-9cfb-42ee8d6381e5-0001c598", -1),
        (121706, "ElectricalSetting", "c0291ff6-b69d-41be-b564-9256657cb216-0001db6a", -1),
        (121707, "SunAnnotationElem", "c0291ff6-b69d-41be-b564-9256657cb216-0001db6b", -2009609),
        (121716, "ConceptualConstructionType", "584be8a3-b212-45c9-9779-0cdbc232fa66-0001db74", -2003421),
        (121725, "ConceptualConstructionType", "584be8a3-b212-45c9-9779-0cdbc232fa66-0001db7d", -2003414),
        (121732, "ConceptualConstructionType", "584be8a3-b212-45c9-9779-0cdbc232fa66-0001db84", -2003422),
        (121739, "ConceptualConstructionType", "584be8a3-b212-45c9-9779-0cdbc232fa66-0001db8b", -2003418),
        (121740, "ConceptualConstructionType", "584be8a3-b212-45c9-9779-0cdbc232fa66-0001db8c", -2003423),
        (121751, "ConceptualConstructionType", "584be8a3-b212-45c9-9779-0cdbc232fa66-0001db97", -2003417),
        (121752, "ConceptualSurfaceType", "584be8a3-b212-45c9-9779-0cdbc232fa66-0001db98", -1),
        (121762, "ListConceptualSurfaceTypes", "584be8a3-b212-45c9-9779-0cdbc232fa66-0001dba2", -1),
        (125028, "FamilySymbol", "7a60b0e9-ccff-4a92-bcce-eafa6d93788e-0001e864", -2006045),
        (127547, "ParamElemElectricalLoadClassification", "31ad5b18-19aa-4bc3-aa07-362dc06e3c81-0001f23b", -2008148),
        (130510, "MEPSystemTracker", "d3af8c34-2c46-4e7f-acc7-f08b4d2d26c3-0001fdce", -1),
        (130813, "WorksharingViewModeSettings", "44173bbd-2445-46cc-b0a3-8d5b4cfda33c-0001fefd", -1),
        (132328, "AssemblyTracker", "2e2218ce-7ae8-4c53-826a-5bf15e536708-000204e8", -1),
        (132466, "HubsTracker", "2e2218ce-7ae8-4c53-826a-5bf15e536708-00020572", -1),
        (132467, "RbsHvacSystemType", "2e2218ce-7ae8-4c53-826a-5bf15e536708-00020573", -2008015),
        (132471, "RbsPipingSystemType", "2e2218ce-7ae8-4c53-826a-5bf15e536708-00020577", -2008043),
        (132481, "RvtLinkInstanceAppearanceParentElem", "2e2218ce-7ae8-4c53-826a-5bf15e536708-00020581", -1),
        (132484, "SketchGridAppearanceParentElem", "2e2218ce-7ae8-4c53-826a-5bf15e536708-00020584", -1),
        (133303, "InitialViewSettings", "4f286005-d8cb-4997-a507-a9a76a50f372-000208b7", -1),
        (133589, "PropertySetLibrary", "e4b33063-2728-4a99-a745-d322bb875315-000209d5", -1),
        (137223, "ComponentRepeaterSlotSpecialType", "a2c1dc4a-7dc4-4a1a-8894-f6ea4b0bba55-00021807", -1),
        (137322, "AnalyticalLinkType", "c7dfb014-08c7-426e-b3e5-26576aa11b01-0002186a", -2009657),
        (137323, "PipeSegment", "c7dfb014-08c7-426e-b3e5-26576aa11b01-0002186b", -2008163),
        (137426, "ReinforcementSettings", "ab3cce22-757c-4e06-b0cd-b32cd22cc403-000218d2", -1),
        (139202, "HandRailType", "9020f828-ce55-4f5d-9922-27877ae62719-00021fc2", -2000947),
        (141548, "TopRailType", "9020f828-ce55-4f5d-9922-27877ae62719-000228ec", -2000946),
        (141723, "CutMarkType", "9020f828-ce55-4f5d-9922-27877ae62719-0002299b", -2000930),
        (141728, "StairsPathType", "9020f828-ce55-4f5d-9922-27877ae62719-000229a0", -2000938),
        (141730, "StairsRunType", "9020f828-ce55-4f5d-9922-27877ae62719-000229a2", -2000919),
        (141731, "StairsLandingType", "9020f828-ce55-4f5d-9922-27877ae62719-000229a3", -2000920),
        (141732, "StairsSupportType", "9020f828-ce55-4f5d-9922-27877ae62719-000229a4", -2000123),
        (141734, "StairsType", "9020f828-ce55-4f5d-9922-27877ae62719-000229a6", -2000120),
        (144211, "AppearanceAssetElem", "1d113b1b-0ab8-42fd-ba1b-155d8cb40783-00023353", -1),
        (147064, "FamilySymbol", "9c7bf9db-3143-4fa2-9339-88d4d04d233d-00023e78", -2000948),
        (147514, "FamSymSurrogate", "23afd781-9f1e-4b22-8309-8c5afbe244cf-0002403a", -1),
        (148346, "FilledRegionAttributes", "23afd781-9f1e-4b22-8309-8c5afbe244cf-0002437a", -1),
        (174475, "PropertySetElement", "88566e0d-6daf-48ce-b050-ac0348546712-0002a98b", -2000924),
        (176803, "VarSketch", "203cff26-4c9d-4eb4-93e5-28d28a01f256-0002b2a3", -1),
        (176804, "Floor", "203cff26-4c9d-4eb4-93e5-28d28a01f256-0002b2a4", -2000032),
        (195169, "Grid", "ef57b02a-5e81-49e7-93bb-ae5f002d921c-0002fa61", -2000220),
        (198366, "FamilySymbol", "ef57b02a-5e81-49e7-93bb-ae5f002d921c-000306de", -2000014),
        (198694, "SWall", "ef57b02a-5e81-49e7-93bb-ae5f002d921c-00030826", -2000011),
        (211807, "FamilySymbol", "88b03b05-80d3-484f-8022-471f61604c8d-00033b5f", -2001160),
        (211850, "FamilyInstance", "88b03b05-80d3-484f-8022-471f61604c8d-00033b8a", -2001160),
        (232770, "SysMullionFamSym", "88b03b05-80d3-484f-8022-471f61604c8d-00038d42", -2000171),
        (232780, "FamilySymbol", "88b03b05-80d3-484f-8022-471f61604c8d-00038d4c", -2000023),
        (243253, "Section", "a6aa132d-ccd7-408f-b2f9-ed67350c8c3a-0003b635", -2000278),
        (243274, "Roof", "a6aa132d-ccd7-408f-b2f9-ed67350c8c3a-0003b64a", -2000035),
        (243275, "RefPlane", "a6aa132d-ccd7-408f-b2f9-ed67350c8c3a-0003b64b", -2000530),
        (286091, "ImageHolder", "32c6aad3-999a-4b41-9ecd-2451f2cb35d0-00045d8b", -1),
        (286092, "ImageSymbol", "32c6aad3-999a-4b41-9ecd-2451f2cb35d0-00045d8c", -2000560),
        (287141, "FamilySymbol", "32c6aad3-999a-4b41-9ecd-2451f2cb35d0-000461a5", -2000151),
        (382615, "FamilySymbol", "6d3acd40-53b7-41b4-9d96-72e9eaf4bc89-0005d697", -2000280),
        (382617, "DBViewDrafting", "6d3acd40-53b7-41b4-9d96-72e9eaf4bc89-0005d699", -2003100),
        (382621, "FamilyInstance", "6d3acd40-53b7-41b4-9d96-72e9eaf4bc89-0005d69d", -2000280),
        (397393, "FamilySymbol", "73f93e8c-79ed-4339-b869-a6f220073e64-00061051", -2001260),
        (411452, "SiteSurface", "ef4ad1cb-015c-4bb6-9030-e7bb82f1dd34-0006473c", -2001340),
        (414482, "FamilyInstance", "ef4ad1cb-015c-4bb6-9030-e7bb82f1dd34-00065312", -2001330),
        (414737, "BuildingPadType", "ef4ad1cb-015c-4bb6-9030-e7bb82f1dd34-00065411", -2001263),
        (415653, "CurveElem", "ef4ad1cb-015c-4bb6-9030-e7bb82f1dd34-000657a5", -2000045),
        (418977, "FamilyInstance", "d85573c2-f8d5-46ae-966a-ac82fa18f500-000664a1", -2001260),
        (420952, "FamilySymbol", "81595c38-a4c2-4a22-9ddf-5884d5e7382c-00066c58", -2001330),
        (422466, "FamilyInstance", "59371552-5800-43eb-9ba3-609565158fc5-00067242", -2000023),
        (423100, "FamilyInstance", "59371552-5800-43eb-9ba3-609565158fc5-000674bc", -2000170),
        (423106, "CurtainGridLine", "59371552-5800-43eb-9ba3-609565158fc5-000674c2", -2000321),
        (423134, "FamilyInstance", "59371552-5800-43eb-9ba3-609565158fc5-000674de", -2000171),
        (423812, "CurtaSystemFaceManager", "59371552-5800-43eb-9ba3-609565158fc5-00067784", -1),
        (429028, "Alignment", "c85e5be0-d8d5-4148-836f-b55e711ef373-00068be4", -2000262),
        (457479, "FamilyInstance", "6cbabf1d-e8d0-47f0-ac4d-9a7923128d37-0006fb07", -2000014),
        (458927, "NewCurtainWallType", "6cbabf1d-e8d0-47f0-ac4d-9a7923128d37-000700af", -2000011),
        (490129, "StairsRailingAttr", "4269d745-d78a-4cca-860f-a55b170715ad-00077a91", -2000126),
        (490319, "BuildingPad", "d9a4583b-3667-47f2-9136-29137bbc6cf9-00077b4f", -2001263),
        (495354, "RegenSplitterElem", "17836a3c-e764-47fa-a2e0-08216444f621-00078efa", -1),
        (499496, "FamilyInstance", "1ac40ecd-9b56-404a-8189-99248e0832e3-00079f28", -2000151),
        (503760, "ViewSheetSet", "96cdd175-e436-4cac-a08f-bf0fabb86ac5-0007afd0", -1),
        (504396, "SWallRectOpening", "96834e54-f091-4317-9b1a-bbe8b647201d-0007b24c", -2000997),
        (506161, "ExtrusionElem", "96834e54-f091-4317-9b1a-bbe8b647201d-0007b931", -1),
        (506166, "FamilySymbol", "96834e54-f091-4317-9b1a-bbe8b647201d-0007b936", -2001100),
        (506168, "FamilyInstance", "96834e54-f091-4317-9b1a-bbe8b647201d-0007b938", -2001100),
        (507024, "RoofAttributes", "96834e54-f091-4317-9b1a-bbe8b647201d-0007bc90", -2000035),
        (509468, "FamilySymbol", "656284ae-d84a-4c5c-ae16-d638fa151c7e-0007c61c", -2001300),
        (512277, "FamilyInstance", "656284ae-d84a-4c5c-ae16-d638fa151c7e-0007d115", -2001300),
        (513254, "StairsElement", "079d150e-b61b-46bb-a991-2bbcfc2d317a-0007d4e6", -2000120),
        (513309, "StairsRun", "079d150e-b61b-46bb-a991-2bbcfc2d317a-0007d51d", -2000919),
        (513310, "StairsTriserSymbol", "079d150e-b61b-46bb-a991-2bbcfc2d317a-0007d51e", -2000921),
        (513460, "StairsPathElement", "079d150e-b61b-46bb-a991-2bbcfc2d317a-0007d5b4", -2000938),
        (549060, "LinearDimString", "5e8712a2-e372-44b6-9e73-0aaa430558ee-000860c4", -2000261),
        (566588, "BaseRailing", "79010029-8aa9-4c91-b2c8-b7f94cb6df11-0008a53c", -2000126),
        (566589, "BaseRailingSym", "79010029-8aa9-4c91-b2c8-b7f94cb6df11-0008a53d", -1),
        (566599, "FamilyInstance", "79010029-8aa9-4c91-b2c8-b7f94cb6df11-0008a547", -2000127),
        (566600, "TopRail", "79010029-8aa9-4c91-b2c8-b7f94cb6df11-0008a548", -2000946),
        (566601, "ContinuousRailSymbol", "79010029-8aa9-4c91-b2c8-b7f94cb6df11-0008a549", -1),
        (566602, "Path3d", "79010029-8aa9-4c91-b2c8-b7f94cb6df11-0008a54a", -1),
        (566609, "CurveElem", "79010029-8aa9-4c91-b2c8-b7f94cb6df11-0008a551", -2000954),
        (566610, "ProfileRefPlane", "79010029-8aa9-4c91-b2c8-b7f94cb6df11-0008a552", -2000530),
        (626581, "ContourLabelingElem", "5b7ac240-e9a7-43b2-b6bf-246956549a40-00098f95", -2000350),
        (628357, "FamilySymbol", "5b7ac240-e9a7-43b2-b6bf-246956549a40-00099685", -2003400),
        (628359, "FamilyInstance", "5b7ac240-e9a7-43b2-b6bf-246956549a40-00099687", -2003400),
        (628366, "FormElem", "5b7ac240-e9a7-43b2-b6bf-246956549a40-0009968e", -2003404),
        (628367, "ReferencePoint", "5b7ac240-e9a7-43b2-b6bf-246956549a40-0009968f", -2000710),
        (628369, "NonPlanarSketch", "5b7ac240-e9a7-43b2-b6bf-246956549a40-00099691", -1),
        (668628, "CurveElem", "a3bcf3f5-e669-4d93-9730-faa0d5bf737d-000a33d4", -2000067),
        (668634, "CurveElem", "a3bcf3f5-e669-4d93-9730-faa0d5bf737d-000a33da", -2000068),
        (668651, "CurveElem", "a3bcf3f5-e669-4d93-9730-faa0d5bf737d-000a33eb", -2000945),
        (669213, "LinearDimString", "a3bcf3f5-e669-4d93-9730-faa0d5bf737d-000a361d", -2000260),
        (677680, "FamilySymbol", "89b7d9e7-00a9-45d9-8498-e33a7de65df7-000a5730", -2001350),
        (677695, "FamilyInstance", "89b7d9e7-00a9-45d9-8498-e33a7de65df7-000a573f", -2001350),
        (690047, "FamilySymbol", "89b7d9e7-00a9-45d9-8498-e33a7de65df7-000a877f", -2001000),
        (690080, "FamilyInstance", "89b7d9e7-00a9-45d9-8498-e33a7de65df7-000a87a0", -2001000),
        (693257, "RbsElectricalSystem", "f9b0887c-fa64-4a05-bfea-7b28c0d4a171-000a9409", -2008037),
        (697449, "SweepElem", "f9b0887c-fa64-4a05-bfea-7b28c0d4a171-000aa469", -1),
        (697612, "Ceiling", "f714ef06-3fa3-4b95-b3e7-bbbd1e6eda49-000aa50c", -2000038),
        (699849, "Viewer3d", "632394c2-b156-41ec-bb3d-3c4e91306cb2-000aadc9", -2000500),
        (699850, "ModelClipBox", "632394c2-b156-41ec-bb3d-3c4e91306cb2-000aadca", -2000301),
        (699851, "DBView3d", "632394c2-b156-41ec-bb3d-3c4e91306cb2-000aadcb", -2000279),
        (699855, "LightSchemeElement", "632394c2-b156-41ec-bb3d-3c4e91306cb2-000aadcf", -1),
        (724430, "ProfileRoof", "1551350c-45e5-41b8-bdf1-bcfa6794dc6a-000b0dce", -2000035),
        (725396, "StairsLanding", "57f8477a-60e4-4504-911c-957c1b7fc5d4-000b1194", -2000920),
        (735724, "FamilySymbol", "57f8477a-60e4-4504-911c-957c1b7fc5d4-000b39ec", -2000080),
        (735739, "FamilyInstance", "57f8477a-60e4-4504-911c-957c1b7fc5d4-000b39fb", -2000080),
        (754016, "FamilySymbol", "e74fbea1-1e07-45a5-b4f5-538a6110ed43-000b8160", -2001120),
        (757198, "FamilyInstance", "df146ad3-a574-4183-b3dd-a6f6589001b3-000b8dce", -2001120),
        (766598, "Floor", "c82e0dd9-31e8-407e-a294-c30431c2fda3-000bb286", -2001300),
        (776839, "FamilySymbol", "05305b4b-b8cf-4c6b-ab67-2f5e121c1c6f-000bda87", -2001040),
        (778316, "FamilyInstance", "05305b4b-b8cf-4c6b-ab67-2f5e121c1c6f-000be04c", -2001040),
        (800213, "ElementGroup", "d5881ba7-c6fb-4696-aebc-8d7ab6f02f63-000c35d5", -2000095),
        (800214, "ElementGroupType", "d5881ba7-c6fb-4696-aebc-8d7ab6f02f63-000c35d6", -2000095),
        (800282, "Pattern", "d5881ba7-c6fb-4696-aebc-8d7ab6f02f63-000c361a", -2000091),
        (800283, "LinearDimString", "d5881ba7-c6fb-4696-aebc-8d7ab6f02f63-000c361b", -2000262),
        (857184, "FamilySymbol", "e6ac360b-aaed-4c3b-a130-36b4c2ac9d13-000d1460", -2000480),
        (857191, "RoomElem", "e6ac360b-aaed-4c3b-a130-36b4c2ac9d13-000d1467", -2000160),
        (857192, "RoomTag", "e6ac360b-aaed-4c3b-a130-36b4c2ac9d13-000d1468", -2000480),
        (857222, "DBViewTypeAreaPlan", "e6ac360b-aaed-4c3b-a130-36b4c2ac9d13-000d1486", -1),
        (857234, "CurveElem", "e6ac360b-aaed-4c3b-a130-36b4c2ac9d13-000d1492", -2000079),
        (857330, "LevelRoomPlan", "e6ac360b-aaed-4c3b-a130-36b4c2ac9d13-000d14f2", -1),
        (857393, "CurveElem", "e6ac360b-aaed-4c3b-a130-36b4c2ac9d13-000d1531", -2000066),
        (859086, "FamilySymbol", "e6ac360b-aaed-4c3b-a130-36b4c2ac9d13-000d1bce", -2000450),
        (862910, "FamilySymbol", "b9aeca9b-45cb-4d6c-9f11-c9a50b11f25e-000d2abe", -2000460),
        (929139, "FamilyInstance", "dda0c528-df04-4b54-b52b-7838a5b0f937-000e2d73", -2002000),
        (940773, "TextNote", "78e9eb69-8d35-4a8d-b6a4-a44b58d40be2-000e5ae5", -2000300),
        (940871, "IndependentTag", "78e9eb69-8d35-4a8d-b6a4-a44b58d40be2-000e5b47", -2000450),
        (940956, "IndependentTag", "78e9eb69-8d35-4a8d-b6a4-a44b58d40be2-000e5b9c", -2000460),
        (941324, "ColorFillLegend", "0712468e-1b41-4ca5-b1a4-550147bf5c41-000e5d0c", -2000550),
        (941493, "CurveElem", "0712468e-1b41-4ca5-b1a4-550147bf5c41-000e5db5", -2000051),
        (941864, "FamilySymbol", "0712468e-1b41-4ca5-b1a4-550147bf5c41-000e5f28", -2000150),
        (941866, "FamilyInstance", "0712468e-1b41-4ca5-b1a4-550147bf5c41-000e5f2a", -2000150),
        (945990, "FamilySymbol", "ae8b4e75-e47d-473a-8255-a4634e701f79-000e6f46", -2001360),
        (946164, "FamilyInstance", "ae8b4e75-e47d-473a-8255-a4634e701f79-000e6ff4", -2001360),
        (947498, "Property", "ae8b4e75-e47d-473a-8255-a4634e701f79-000e752a", -2001265),
        (948077, "PropertyLineSegment", "ae8b4e75-e47d-473a-8255-a4634e701f79-000e776d", -2001268),
        (948584, "FamilySymbol", "ae8b4e75-e47d-473a-8255-a4634e701f79-000e7968", -2001269),
        (948595, "IndependentTag", "ae8b4e75-e47d-473a-8255-a4634e701f79-000e7973", -2001269),
        (950216, "FamilySymbol", "ae8b4e75-e47d-473a-8255-a4634e701f79-000e7fc8", -2001370),
        (950367, "FamilyInstance", "ae8b4e75-e47d-473a-8255-a4634e701f79-000e805f", -2001370),
        (957747, "PostedWarningElem", "049a22ea-f756-4e88-869b-98c96a808f26-000e9d33", -1),
        (957750, "FaceSplitter", "049a22ea-f756-4e88-869b-98c96a808f26-000e9d36", -2000192),
        (959386, "FamilySymbol", "666da4f5-76ba-443a-9fe6-0bcd8a1bcfe9-000ea39a", -2005021),
        (959432, "IndependentTag", "666da4f5-76ba-443a-9fe6-0bcd8a1bcfe9-000ea3c8", -2005021),
        (959491, "DBViewSchedule", "666da4f5-76ba-443a-9fe6-0bcd8a1bcfe9-000ea403", -2000573),
        (959497, "ScheduleInstance", "666da4f5-76ba-443a-9fe6-0bcd8a1bcfe9-000ea409", -2000570),
        (960064, "DBViewRendering", "1ee397e5-f3e7-4a90-8412-3f9d5b6c781a-000ea640", -2000279),
        (960072, "ImageInstance", "1ee397e5-f3e7-4a90-8412-3f9d5b6c781a-000ea648", -2000560),
        (970620, "CurveElem", "fd0e9d64-8956-40b8-9b79-1aeb51d0650c-000ecf7c", -2000077),
        (974663, "DefaultDivideSettings", "5de47e85-f1ce-4dd7-9a1a-e25cc251c808-000edf47", -1),
        (974907, "DivisionRule", "5de47e85-f1ce-4dd7-9a1a-e25cc251c808-000ee03b", -2008190),
        (974924, "MEPComponentTracker", "5de47e85-f1ce-4dd7-9a1a-e25cc251c808-000ee04c", -1),
        (974927, "AbsDuctType", "5de47e85-f1ce-4dd7-9a1a-e25cc251c808-000ee04f", -2008000),
        (974930, "AbsFlexDuctType", "5de47e85-f1ce-4dd7-9a1a-e25cc251c808-000ee052", -2008020),
        (974932, "RbsPipeType", "5de47e85-f1ce-4dd7-9a1a-e25cc251c808-000ee054", -2008044),
        (974933, "RbsFlexPipeType", "5de47e85-f1ce-4dd7-9a1a-e25cc251c808-000ee055", -2008050),
        (974934, "RbsCableTrayType", "5de47e85-f1ce-4dd7-9a1a-e25cc251c808-000ee056", -2008130),
        (974938, "RbsConduitType", "5de47e85-f1ce-4dd7-9a1a-e25cc251c808-000ee05a", -2008132),
        (974956, "KeynoteTagsOnSheetsTracker", "5de47e85-f1ce-4dd7-9a1a-e25cc251c808-000ee06c", -1),
        (974959, "LegendComponent", "5de47e85-f1ce-4dd7-9a1a-e25cc251c808-000ee06f", -2000576),
        (975044, "RevisionCloudsOnSheetsTracker", "5de47e85-f1ce-4dd7-9a1a-e25cc251c808-000ee0c4", -1),
        (1002131, "LayoutNodesTracker", "0bfbc3fc-18df-451c-9be1-0d3b955eb870-000f4a93", -1),
        (1002459, "AssemblyCodeTable", "0bfbc3fc-18df-451c-9be1-0d3b955eb870-000f4bdb", -1),
        (1002460, "FabricationSettings", "0bfbc3fc-18df-451c-9be1-0d3b955eb870-000f4bdc", -1),
        (1002462, "NumberingSchema", "0bfbc3fc-18df-451c-9be1-0d3b955eb870-000f4bde", -1),
        (1077107, "FabricationSettingsElement", "e969d72a-2583-4a90-8911-c1950fa4d24b-00106f73", -1),
        (1085200, "StructuralConnectionSettings", "3f2a4315-01e5-475e-96a8-363d22f66ea9-00108f10", -1),
        (1086641, "MEPAnalyticalConnectionElementType", "333c7fcc-339f-4a24-b2c3-32c4268bf903-001094b1", -2000983),
        (1086642, "MEPNetworkTracker", "333c7fcc-339f-4a24-b2c3-32c4268bf903-001094b2", -1),
        (1092955, "MechanicalEquipmentSetType", "db42cf78-ee89-473e-939f-b170ea38a8b5-0010ad5b", -2000985),
        (1094660, "MEPSystemZoneType", "961a3fb2-7a5e-4e1d-a1da-32e938a2dc11-0010b404", -2001001),
        (1094665, "RouteAnalysisSettings", "961a3fb2-7a5e-4e1d-a1da-32e938a2dc11-0010b409", -1),
        (1101888, "ArcWall", "01ce1928-1585-4006-b766-c433c9c35299-0010d040", -2000011),
        (1102565, "CircuitNamingTypeSetting", "db45d36d-7001-406a-ad65-377c2aacfb9d-0010d2e5", -1),
        (1102639, "MEPHiddenLineSettingsElem", "db45d36d-7001-406a-ad65-377c2aacfb9d-0010d32f", -1),
        (1102645, "MultipleValuesIndicationSettings", "db45d36d-7001-406a-ad65-377c2aacfb9d-0010d335", -1),
        (1102649, "RevisionNumberingSequence", "db45d36d-7001-406a-ad65-377c2aacfb9d-0010d339", -2006071),
        (1102861, "GraphicsCache", "1f01220e-0b15-402f-8a4f-d8565cc41524-0010d40d", -1),
        (1102919, "AnalyticalToPhysicalRelationManager", "1f01220e-0b15-402f-8a4f-d8565cc41524-0010d447", -1),
        (1102920, "AnalyticalMember", "1f01220e-0b15-402f-8a4f-d8565cc41524-0010d448", -2009662),
        (1102921, "AnalyticalPanel", "1f01220e-0b15-402f-8a4f-d8565cc41524-0010d449", -2009664),
        (1102937, "AnalyticalCurvedPanel", "1f01220e-0b15-402f-8a4f-d8565cc41524-0010d459", -2009664),
        (1103022, "MEPNetworkDataElem", "1f01220e-0b15-402f-8a4f-d8565cc41524-0010d4ae", -1),
        (1103026, "FabricationServiceSettings", "1f01220e-0b15-402f-8a4f-d8565cc41524-0010d4b2", -1),
        (1103029, "ToposolidType", "1f01220e-0b15-402f-8a4f-d8565cc41524-0010d4b5", -2001079),
        (1103031, "ScheduleInstanceBaseOnSheetsTracker", "1f01220e-0b15-402f-8a4f-d8565cc41524-0010d4b7", -1),
        (1103033, "RebarBendingDetailType", "1f01220e-0b15-402f-8a4f-d8565cc41524-0010d4b9", -2001104),
        (1103042, "SSEPointVisibilitySettings", "1f01220e-0b15-402f-8a4f-d8565cc41524-0010d4c2", -1),
        (1103052, "STEPExportSettings", "1f01220e-0b15-402f-8a4f-d8565cc41524-0010d4cc", -1),
        (1103216, "BuildingOperatingYearSchedule", "1f01220e-0b15-402f-8a4f-d8565cc41524-0010d570", -2008121),
        (1103243, "IFCCategoryTemplate", "1f01220e-0b15-402f-8a4f-d8565cc41524-0010d58b", -1),
        (1103245, "SheetsInSheetCollectionTracker", "1f01220e-0b15-402f-8a4f-d8565cc41524-0010d58d", -1),
        (1103418, "Hub", "1f01220e-0b15-402f-8a4f-d8565cc41524-0010d63a", -2009645),
        (1103419, "ReferencePoint", "1f01220e-0b15-402f-8a4f-d8565cc41524-0010d63b", -2009645),
    ];

    private sealed class TestFile2025
    {
        public RevitFileInfo Info { get; } = new RvtFileReader().Read(TestFileFixture.FindTestFile("Test_2025.rvt"));
    }

    private static readonly Lazy<TestFile2025> File2025 = new(() => new TestFile2025());

    [SkippableFact]
    public void Test2025_RootClassUniqueIdAndHeaderCategoryAgreeWithReference()
    {
        var byId = File2025.Value.Info.Elements.Where(e => !e.IsBlockOnly).GroupBy(e => e.Id).ToDictionary(g => g.Key, g => g.First());
        foreach (var v in MeshVectors)
        {
            Assert.True(byId.TryGetValue(v.Id, out var element), $"element {v.Id} missing");
            Assert.Equal(v.Class, element!.NativeRootClassName);
            Assert.Equal(v.UniqueId, element!.UniqueId);
            Assert.Equal(v.Category, element.NativeHeaderCategoryId);
        }
    }

    [SkippableFact]
    public void Test2025_EveryElementTableRowHasAClassAndAHeaderCategory()
    {
        var elements = File2025.Value.Info.Elements.Where(e => !e.IsBlockOnly && e.UniqueId != null && e.NativeRootClassName != null).ToList();
        Assert.True(elements.Count >= 8342, $"{elements.Count} elements with a root class");
        Assert.All(elements, e => Assert.NotNull(e.NativeHeaderCategoryId));
        // the Revit 2022 class number of the class of the same NAME (the rules are written against those numbers); none for a class 2022 lacks
        Assert.All(elements, e => Assert.True(e.SerializedOwnElementClassId is not ushort tag
            ? !TfBlockDeserializer.Schemas.Values.Any(c => c.Name == e.NativeRootClassName)
            : TfBlockDeserializer.Schemas[tag].Name == e.NativeRootClassName));
    }

    [Theory]
    [InlineData(false, -2000112)]
    [InlineData(false, -1)]
    [InlineData(true, -2000112)]
    [InlineData(true, -1)]
    public void BuildElemTableElement_ResolvesZeroIdProjectPhaseInBothModesAndWithMinusOneHeader(bool r22View, int headerCategory)
    {
        var state = new ParsingState
        {
            WideSchema = new FileSchema(new SchemaDocument()),
            R22View = r22View,
            ElemTable = new Dictionary<uint, uint> { [0] = uint.MaxValue },
            ElemRecOwningElementIds = new Dictionary<uint, int> { [0] = -1 },
        };
        state.HeaderCategories[0] = headerCategory;
        state.NativeRootClassNames[0] = "ProjectPhase";
        state.WideNames[0] = "Existing";
        state.OwnedElementClassRoots[0] = new(1, 0x0BF9);
        state.WalkedElementClassRoots[0] = new(1, 0x0BF9);
        state.ElemClassIdx[0] = 0x0BF9;
        state.ProjectPhaseOrderSource = (1, [0]);

        var info = new RevitFileInfo();
        ElementBuilder.BuildElemTableElement(0, uint.MaxValue, state, info);
        ElementBuilder.PostProcess(info, state);
        ElementBuilder.ApplySerializedSpatialPhases(info, state);
        StairComponentSource.Apply(info.Elements, state);

        var phase = Assert.Single(info.Elements);
        Assert.Equal(0, phase.Id);
        Assert.Equal("ProjectPhase", phase.RootClassName);
        Assert.Equal(-2000112, phase.CategoryId);
        Assert.Equal(BuiltInCategoryNames.Lookup[-2000112], phase.Category);
        Assert.Equal("Existing", phase.Name);
        Assert.Equal(-2000112, phase.Parameters.FirstOrDefault(p => p.Id == -1140362)?.Value);
        Assert.Equal(-2000112, phase.Parameters.FirstOrDefault(p => p.Id == -1140363)?.Value);
        Assert.Equal((ushort)0x0BF9, phase.SerializedOwnElementClassId);
        Assert.True(phase.OwnClassRootSeededOnce);
        Assert.Equal(0, ProductRules.LastProjectPhase(info));
        Assert.Single(new FilteredElementCollector(info.Elements).OfCategory(BuiltInCategory.OST_Phases));

        int named = ValidatedElementNames.Apply(info, state);
        Assert.Equal(1, named);
        Assert.Equal("Existing", phase.Name);
    }
}
