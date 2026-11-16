using System.Text;
using OpenMcdf;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

/// <summary>The file's own class schema (Formats/Latest) and the ElemTable layout derived from it, for any Revit version.</summary>
public class FormatsSchemaTests
{
    /// <summary>Writes top-level class definitions of the Formats/Latest grammar; ids start at 12.</summary>
    private sealed class SchemaBuilder
    {
        private readonly MemoryStream _stream = new();
        private readonly BinaryWriter _w;
        public SchemaBuilder() { _w = new BinaryWriter(_stream); }

        public SchemaBuilder Class(string name, int baseId, uint version, params (string Name, byte Type, byte Mods, int Ref)[] fields)
        {
            _w.Write((ushort)0);
            Name16(name);
            _w.Write((ushort)baseId);
            _w.Write(version);
            _w.Write((uint)fields.Length);
            foreach (var f in fields)
            {
                _w.Write((uint)f.Name.Length);
                _w.Write(Encoding.ASCII.GetBytes(f.Name));
                _w.Write(f.Type); _w.Write(f.Mods); _w.Write((ushort)0);
                if (f.Type == 0x0E && (f.Mods & 0x0F) == 0) _w.Write((ushort)f.Ref);
            }
            _w.Write(0u);
            return this;
        }

        private void Name16(string name) { _w.Write((ushort)name.Length); _w.Write(Encoding.ASCII.GetBytes(name)); }

        public byte[] Build(int padding = 8) => [.. _stream.ToArray(), .. new byte[padding]];
    }

    /// <summary>The classes the ElemTable layout needs, in the order and shape of Revit 2022 (28-byte rows, 4-byte ids) or
    /// Revit 2024 and later (40-byte rows, 8-byte ids, m_history declared first). ElemTable is id 19.</summary>
    internal static byte[] ElemTableSchema(bool wideIds, int? markerId = null)
    {
        byte idType = wideIds ? (byte)0x0B : (byte)0x04;
        string idName = wideIds ? "m_id64" : "m_id";
        var b = new SchemaBuilder()
            .Class("Identifier", 0, 1, (idName, idType, 0, 0))                                   // 12
            .Class("ElementId", 0, 1, ("m_id", 0x0E, 0, 12))                                      // 13
            .Class("OriginalElementId", 0, 1, (idName, idType, 0, 0))                             // 14
            .Class("EpisodeId", 0, 1, ("m_id", 0x04, 0, 0))                                       // 15
            .Class("ElementHistory", 0, 8, ("m_originalElementId", 0x0E, 0, 14), ("m_creationDate", 0x0E, 0, 15),
                ("m_lastModificationDate", 0x0E, 0, 15), ("m_lastUserModificationDate", 0x0E, 0, 15)) // 16
            .Class("PartitionId", 0, 1, ("m_id", 0x04, 0, 0));                                    // 17
        (wideIds
                ? b.Class("ElemRec", 0, 10, ("m_history", 0x0E, 0, 16), ("m_id", 0x0E, 0, 13), ("m_OwningElementId", 0x0E, 0, 13), ("m_partitionId", 0x0E, 0, 17))
                : b.Class("ElemRec", 0, 10, ("m_id", 0x0E, 0, 13), ("m_history", 0x0E, 0, 16), ("m_partitionId", 0x0E, 0, 17), ("m_OwningElementId", 0x0E, 0, 13)))   // 18
            .Class("ElemTable", 0, 9, ("m_elemArr", 0x0E, 0x50, 18), ("m_bExpandAllOnLoad", 0x01, 0, 0));                                                         // 19
        if (markerId is int marker)
        {
            for (int id = 20; id < marker; id++) b.Class("Filler" + id, 0, 1);
            foreach (string name in new[] { "ContentMarker", "ContentKey", "SegmentMarker", "SegmentCheckback", "SignatureMarker" })
                b.Class(name, 0, 1);
        }
        return b.Build();
    }

    [Theory]
    [InlineData(779, 2020)]
    [InlineData(801, 2021)]
    [InlineData(817, 2022)]
    [InlineData(931, 2026)]
    [InlineData(500, null)]
    public void TheSchemaReleaseComesFromTheClassIdOfTheFramingMarker(int markerId, int? release)
    {
        var b = new SchemaBuilder();
        for (int id = 12; id < markerId; id++) b.Class("Filler" + id, 0, 1);
        var schema = FormatsSchema.TryParse(b.Class("ContentMarker", 0, 1).Build())!;
        Assert.Equal(markerId, schema.IdOf("ContentMarker"));
        Assert.Equal(release, SchemaRelease.FromSchema(schema));
    }

    [Fact]
    public void ParsesTopLevelAndInlineClassesAndBackReferences()
    {
        // A(12){ m_x i32, m_ref -> inline class B(13) }, C(14) : A
        var w = new MemoryStream(); var bw = new BinaryWriter(w);
        void Text(string s) { bw.Write((ushort)s.Length); bw.Write(Encoding.ASCII.GetBytes(s)); }
        bw.Write((ushort)0); Text("A"); bw.Write((ushort)0); bw.Write(3u); bw.Write(2u);
        bw.Write(3u); bw.Write("m_x"u8.ToArray()); bw.Write((byte)4); bw.Write((byte)0); bw.Write((ushort)0);
        bw.Write(5u); bw.Write("m_ref"u8.ToArray()); bw.Write((byte)0x0E); bw.Write((byte)0); bw.Write((ushort)0);
        bw.Write((ushort)(0x8000 | 13)); bw.Write((ushort)0); Text("B"); bw.Write((ushort)0); bw.Write(1u); bw.Write(0u); bw.Write(0u);   // inline B
        bw.Write(0u);                                                                                                                       // A: no guids
        bw.Write((ushort)0); Text("C"); bw.Write((ushort)12); bw.Write(7u); bw.Write(0u); bw.Write(0u);                                    // C : A
        var schema = FormatsSchema.TryParse([.. w.ToArray(), .. new byte[8]]);

        Assert.NotNull(schema);
        Assert.Equal(3, schema.Classes.Count);
        Assert.Equal([12, 13, 14], schema.Classes.Keys.Order());
        Assert.Equal(12, schema.IdOf("A")); Assert.Equal(13, schema.IdOf("B")); Assert.Equal(14, schema.IdOf("C"));
        Assert.Equal(3u, schema.Get("A")!.Version);
        Assert.Equal(new FormatsSchema.Field("m_ref", 0x0E, 0, 13, null), schema.Get("A")!.Fields[1]);
        Assert.Equal(12, schema.Get("C")!.Base);
        Assert.Null(schema.IdOf("D"));
    }

    [Fact]
    public void RejectsAStreamThatBreaksTheGrammar()
    {
        byte[] good = new SchemaBuilder().Class("A", 0, 1, ("m_x", 0x04, 0, 0)).Build();
        Assert.NotNull(FormatsSchema.TryParse(good));
        Assert.Null(FormatsSchema.TryParse(good[..^9]));                                                          // cut inside the stream
        Assert.Null(FormatsSchema.TryParse(new SchemaBuilder().Class("A", 99, 1).Build()));                       // base is not registered
        Assert.Null(FormatsSchema.TryParse(new SchemaBuilder().Class("A", 0, 1, ("m_x", 0x00, 0, 0)).Build()));   // unknown field type
        Assert.Null(FormatsSchema.TryParse(new SchemaBuilder().Class("A", 0, 1, ("m_ref", 0x0E, 0, 13)).Build())); // reference to a class not registered yet
        Assert.Null(FormatsSchema.TryParse([]));
        byte[] marker = (byte[])good.Clone(); marker[0] = 1;
        Assert.Null(FormatsSchema.TryParse(marker));
    }

    [Theory]
    [InlineData(false, 28, 4)]
    [InlineData(true, 40, 8)]
    public void ElemRecWidthAndIdWidthComeFromTheSchema(bool wideIds, int rowLength, int idWidth)
    {
        var schema = FormatsSchema.TryParse(ElemTableSchema(wideIds))!;
        Assert.Equal(rowLength, ElemTableLayout.RowLength(schema));
        Assert.Equal(idWidth, ElemTableLayout.IdWidth(schema));
    }

    [Fact]
    public void InMemoryPointerMembersOfElemRecTakeNoBytes()
    {
        // Revit 2027 declares m_pElement, m_pGRep (pointer flavour 1) and m_pHeader (flavour 2) in ElemRec; the rows stay 40 bytes
        var b = new SchemaBuilder()
            .Class("Identifier", 0, 2, ("m_id64", 0x0B, 0, 0))
            .Class("ElementId", 0, 1, ("m_id", 0x0E, 0, 12))
            .Class("EpisodeId", 0, 1, ("m_id", 0x04, 0, 0))
            .Class("ElementHistory", 0, 8, ("m_originalElementId", 0x0E, 0, 12), ("m_creationDate", 0x0E, 0, 14),
                ("m_lastModificationDate", 0x0E, 0, 14), ("m_lastUserModificationDate", 0x0E, 0, 14))
            .Class("PartitionId", 0, 1, ("m_id", 0x04, 0, 0))
            .Class("ElemRec", 0, 10, ("m_pElement", 0x0E, 1, 0), ("m_pGRep", 0x0E, 1, 0), ("m_pHeader", 0x0E, 2, 0),
                ("m_history", 0x0E, 0, 15), ("m_id", 0x0E, 0, 13), ("m_OwningElementId", 0x0E, 0, 13), ("m_partitionId", 0x0E, 0, 16))
            .Class("ElemTable", 0, 9, ("m_elemArr", 0x0E, 0x50, 17));
        var schema = FormatsSchema.TryParse(b.Build())!;
        Assert.Equal(40, ElemTableLayout.RowLength(schema));
    }

    internal static byte[] Rows(bool wideIds, params (long Id, long Original, int Created, int Partition, long Owner)[] rows)
    {
        var s = new MemoryStream(); var w = new BinaryWriter(s);
        w.Write((ushort)19); w.Write((uint)rows.Length);
        foreach (var r in rows)
        {
            if (wideIds) { w.Write(r.Original); w.Write(r.Created); w.Write(r.Created + 1); w.Write(r.Created + 2); w.Write(r.Id); w.Write(r.Owner); w.Write(r.Partition); }
            else { w.Write((int)r.Id); w.Write((int)r.Original); w.Write(r.Created); w.Write(r.Created + 1); w.Write(r.Created + 2); w.Write(r.Partition); w.Write((int)r.Owner); }
        }
        w.Write(new byte[19]);   // the rest of the ElemTable object
        return s.ToArray();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ElemTableRowsAreReadInTheDeclaredFieldOrder(bool wideIds)
    {
        var schema = FormatsSchema.TryParse(ElemTableSchema(wideIds))!;
        long third = wideIds ? 5_000_000_000 : 1_000_000;
        var rows = ElemTableLayout.TryParse(Rows(wideIds, (1, 1, 0, 0, -1), (700, 500, 64, 7, 1), (third, 9, 3, -1, 700)), schema);
        Assert.NotNull(rows);
        Assert.Equal(3, rows.Count);
        Assert.Equal(new ElemTableLayout.Row(1, 1, 0, 0, -1, 1), rows[0]);
        Assert.Equal(new ElemTableLayout.Row(700, 500, 64, 7, 1, 65), rows[1]);
        Assert.Equal(new ElemTableLayout.Row(third, 9, 3, -1, 700, 4), rows[2]);
    }

    [Theory]
    [InlineData("truncated")] [InlineData("unordered")] [InlineData("duplicate")] [InlineData("wrong class id")] [InlineData("count")]
    public void MalformedElemTablesAreRejected(string mode)
    {
        var schema = FormatsSchema.TryParse(ElemTableSchema(wideIds: true))!;
        byte[] bytes = Rows(true, (10, 10, 0, 0, -1), (20, 20, 0, 0, -1));
        if (mode == "truncated") bytes = bytes[..(6 + 40 + 39)];
        if (mode == "unordered") BitConverter.GetBytes(5L).CopyTo(bytes, 6 + 40 + 20);
        if (mode == "duplicate") BitConverter.GetBytes(10L).CopyTo(bytes, 6 + 40 + 20);
        if (mode == "wrong class id") bytes[0] = 20;
        if (mode == "count") BitConverter.GetBytes(uint.MaxValue).CopyTo(bytes, 2);
        Assert.Null(ElemTableLayout.TryParse(bytes, schema));
    }


    private static string Sample(string name)
    {
        string path = TestFiles.Find(name);
        return new FileInfo(path).Length > 1_000_000 ? path : throw new SkipException($"{name} is a placeholder (smaller than 1 MB), not a project file.");
    }

    [SkippableTheory]
    // file, classes, ElementHeader, ElemTable, id width, ElemTable rows
    [InlineData("Test_2020.rvt", 4164, 1272, 1244, 4, 8014)]
    [InlineData("Test_2022.rvt", 4307, 1340, 1312, 4, 8161)]
    [InlineData("Test_2025.rvt", 4600, 1479, 1451, 8, 8342)]
    public void RealFileSchemaAndElemTableMatchTheMeasuredNumbers(string file, int classes, int elementHeader, int elemTable, int idWidth, int rows)
    {
        string path = Sample(file);
        using var root = RootStorage.OpenRead(path);
        var schema = FormatsSchema.TryRead(root);
        Assert.NotNull(schema);
        Assert.Equal(classes, schema.Classes.Count);
        Assert.Equal(elementHeader, schema.IdOf("ElementHeader"));
        Assert.Equal(elemTable, schema.IdOf("ElemTable"));
        Assert.Equal(idWidth, ElemTableLayout.IdWidth(schema));
        var table = ElemTableLayout.TryParse(PagedGzipStream.Read(root, "Global", "ElemTable")!, schema);
        Assert.NotNull(table);
        Assert.Equal(rows, table.Count);
    }

    [SkippableFact]
    public void ElemTableReaderReadsEveryRowOfARevit2025File()
    {
        string path = Sample("Test_2025.rvt");
        using var root = RootStorage.OpenRead(path);
        var elements = ElemTableReader.Read(root, out var worksets, out var dates, out bool exact);
        Assert.True(exact);
        Assert.Equal(8342, elements.Count);
        Assert.Equal(8342, worksets.Count);
        Assert.Equal(8342, dates.Count);
    }

    [SkippableFact]
    public void EveryMemberOfARevit2025PartitionDecodesExactly()
    {
        string path = Sample("Test_2025.rvt");
        using var root = RootStorage.OpenRead(path);
        var storage = root.OpenStorage("Partitions");
        var entry = Assert.Single(storage.EnumerateEntries(), e => e.Type == EntryType.Stream);
        using var stream = storage.OpenStream(entry.Name);
        var raw = new byte[stream.Length]; stream.ReadExactly(raw);
        var chunks = new ChunkDecompressor().DecompressAll(raw);
        Assert.Equal(1160, chunks.Count);
        Assert.Equal(107_801_490L, chunks.Sum(c => (long)c.Length));
        // the stream starts with the run of element records: [u64 id][u32 size][ElementHeader class id], idWidth + 8 + size long
        long pos = 0; int records = 0;
        var first = chunks[0];
        while (pos + 14 <= first.Length && BitConverter.ToUInt16(first, (int)pos + 12) == 1479 && pos + 16 + BitConverter.ToUInt32(first, (int)pos + 8) <= first.Length)
        { pos += 16 + BitConverter.ToUInt32(first, (int)pos + 8); records++; }
        Assert.True(records > 10);
        Assert.Equal(86291L, BitConverter.ToInt64(first, 0));   // stream order, not id order
    }
}
