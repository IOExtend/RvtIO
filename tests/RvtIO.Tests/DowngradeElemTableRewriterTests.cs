using System.Text;
using OpenMcdf;
using RvtIO.Parsing;
using RvtIO.Writing;
using Xunit;

namespace RvtIO.Tests;

/// <summary>Transcoding a decoded Global/ElemTable stream between element-id widths
/// (40-byte rows with 8-byte ids to 28-byte rows with 4-byte ids).</summary>
public sealed class DowngradeElemTableRewriterTests
{
    /// <summary>Writes top-level class definitions of the Formats/Latest grammar; ids start at 12.</summary>
    private sealed class SchemaBuilder
    {
        private readonly MemoryStream _stream = new();
        private readonly BinaryWriter _w;
        public SchemaBuilder() { _w = new BinaryWriter(_stream); }

        public SchemaBuilder Class(string name, int baseId, uint version, params (string Name, byte Type, byte Mods, int Ref)[] fields) =>
            ClassWithExtra(name, baseId, version, [.. fields.Select(f => (f.Name, f.Type, f.Mods, f.Ref, (ushort)0))]);

        public SchemaBuilder ClassWithExtra(string name, int baseId, uint version, params (string Name, byte Type, byte Mods, int Ref, ushort Extra)[] fields)
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
                _w.Write(f.Type); _w.Write(f.Mods); _w.Write(f.Extra);
                if (f.Type == 0x0E && (f.Mods & 0x0F) == 0) _w.Write((ushort)f.Ref);
            }
            _w.Write(0u);
            return this;
        }

        private void Name16(string name) { _w.Write((ushort)name.Length); _w.Write(Encoding.ASCII.GetBytes(name)); }

        public byte[] Build(int padding = 8) => [.. _stream.ToArray(), .. new byte[padding]];
    }

    /// <summary>The classes the ElemTable layout needs, in Revit 2022 shape (28-byte rows)
    /// or Revit 2024+ shape (40-byte rows, m_history declared first).</summary>
    private static byte[] ElemTableSchema(bool wideIds)
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
        return (wideIds
                ? b.Class("ElemRec", 0, 10, ("m_history", 0x0E, 0, 16), ("m_id", 0x0E, 0, 13), ("m_OwningElementId", 0x0E, 0, 13), ("m_partitionId", 0x0E, 0, 17))
                : b.Class("ElemRec", 0, 10, ("m_id", 0x0E, 0, 13), ("m_history", 0x0E, 0, 16), ("m_partitionId", 0x0E, 0, 17), ("m_OwningElementId", 0x0E, 0, 13))) // 18
            .Class("ElemTable", 0, 9, ("m_elemArr", 0x0E, 0x50, 18), ("m_bExpandAllOnLoad", 0x01, 0, 0))                                                          // 19
            .Build();
    }

    private static byte[] WideRows(params (long Id, long Original, int Created, int Partition, long Owner)[] rows)
    {
        var s = new MemoryStream(); var w = new BinaryWriter(s);
        w.Write((ushort)19); w.Write((uint)rows.Length);
        foreach (var r in rows)
        {
            w.Write(r.Original); w.Write(r.Created); w.Write(r.Created + 1); w.Write(r.Created + 2);
            w.Write(r.Id); w.Write(r.Owner); w.Write(r.Partition);
        }
        w.Write(new byte[19]);   // trailing rest preserved verbatim
        return s.ToArray();
    }

    [Fact]
    public void Transcode_PreservesEveryValue_AndRemapsTheTableId()
    {
        var sourceDoc = TfSchemaReader.Parse(ElemTableSchema(wideIds: true))!;
        var targetDoc = TfSchemaReader.Parse(ElemTableSchema(wideIds: false))!;
        byte[] source = WideRows((101, 101, 5, 1, -1), (202, 200, 9, 2, 101), (700, 700, 3, 1, -1));

        byte[] target = DowngradeElemTableRewriter.Rewrite(source, sourceDoc, targetDoc);

        var rows = ElemTableLayout.TryParse(target, new FormatsSchema(targetDoc));
        Assert.NotNull(rows);
        Assert.Equal(3, rows.Count);
        Assert.Equal(new ElemTableLayout.Row(101, 101, 5, 1, -1, 6), rows[0]);
        Assert.Equal(new ElemTableLayout.Row(202, 200, 9, 2, 101, 10), rows[1]);
        Assert.Equal(new ElemTableLayout.Row(700, 700, 3, 1, -1, 4), rows[2]);
        // Class id remapped, count kept, trailing rest preserved.
        Assert.Equal(19, BitConverter.ToUInt16(target, 0));
        Assert.Equal(3u, BitConverter.ToUInt32(target, 2));
        Assert.Equal(source[^19..], target[^19..]);
        Assert.Equal(6 + 3 * 28 + 19, target.Length);
    }

    [Fact]
    public void Transcode_WithInMemoryPointerFields_StillGives40ByteSourceRows()
    {
        // Revit 2027 declares m_pElement, m_pGRep and m_pHeader in ElemRec; they take no bytes.
        var b = new SchemaBuilder()
            .Class("Identifier", 0, 2, ("m_id64", 0x0B, 0, 0))
            .Class("ElementId", 0, 1, ("m_id", 0x0E, 0, 12))
            .Class("OriginalElementId", 0, 1, ("m_id64", 0x0B, 0, 0))
            .Class("EpisodeId", 0, 1, ("m_id", 0x04, 0, 0))
            .Class("ElementHistory", 0, 8, ("m_originalElementId", 0x0E, 0, 14), ("m_creationDate", 0x0E, 0, 15),
                ("m_lastModificationDate", 0x0E, 0, 15), ("m_lastUserModificationDate", 0x0E, 0, 15))
            .Class("PartitionId", 0, 1, ("m_id", 0x04, 0, 0))
            .ClassWithExtra("ElemRec", 0, 10,
                ("m_pElement", 0x0E, 1, 0, (ushort)2), ("m_pGRep", 0x0E, 1, 0, (ushort)2), ("m_pHeader", 0x0E, 2, 0, (ushort)2),
                ("m_history", 0x0E, 0, 16, (ushort)0), ("m_id", 0x0E, 0, 13, (ushort)0), ("m_OwningElementId", 0x0E, 0, 13, (ushort)0), ("m_partitionId", 0x0E, 0, 17, (ushort)0))
            .Class("ElemTable", 0, 9, ("m_elemArr", 0x0E, 0x50, 18), ("m_bExpandAllOnLoad", 0x01, 0, 0));
        var sourceDoc = TfSchemaReader.Parse(b.Build())!;
        var targetDoc = TfSchemaReader.Parse(ElemTableSchema(wideIds: false))!;
        // Rows follow the declared field order: m_history(20) m_id(8) m_OwningElementId(8) m_partitionId(4).
        var s = new MemoryStream(); var w = new BinaryWriter(s);
        w.Write((ushort)19); w.Write(1u);
        w.Write(101L); w.Write(0); w.Write(1); w.Write(2); w.Write(101L); w.Write(-1L); w.Write(7);
        w.Write((byte)0); // m_bExpandAllOnLoad
        byte[] target = DowngradeElemTableRewriter.Rewrite(s.ToArray(), sourceDoc, targetDoc);
        var rows = ElemTableLayout.TryParse(target, new FormatsSchema(targetDoc));
        Assert.NotNull(rows);
        Assert.Single(rows);
        Assert.Equal(new ElemTableLayout.Row(101, 101, 0, 7, -1, 1), rows[0]);
    }

    [Fact]
    public void Transcode_DroppedOwners_LoseTheirRows_AndTheCountFollows()
    {
        var sourceDoc = TfSchemaReader.Parse(ElemTableSchema(wideIds: true))!;
        var targetDoc = TfSchemaReader.Parse(ElemTableSchema(wideIds: false))!;
        byte[] source = WideRows((101, 101, 5, 1, -1), (202, 200, 9, 2, 101), (700, 700, 3, 1, -1));

        byte[] target = DowngradeElemTableRewriter.Rewrite(source, sourceDoc, targetDoc, new HashSet<ulong> { 202 });

        var rows = ElemTableLayout.TryParse(target, new FormatsSchema(targetDoc));
        Assert.NotNull(rows);
        Assert.Equal([101L, 700L], rows.Select(r => r.Id).ToArray());
        Assert.Equal(2u, BitConverter.ToUInt32(target, 2));
        Assert.Equal(source[^19..], target[^19..]);
        Assert.Equal(6 + 2 * 28 + 19, target.Length);
    }

    [Fact]
    public void Transcode_RefusesIdsBeyond32Bits_WithStableCode()
    {
        var sourceDoc = TfSchemaReader.Parse(ElemTableSchema(wideIds: true))!;
        var targetDoc = TfSchemaReader.Parse(ElemTableSchema(wideIds: false))!;
        byte[] source = WideRows((5_000_000_000L, 9, 3, 1, -1));

        var ex = Assert.Throws<DowngradeRefusedException>(() => DowngradeElemTableRewriter.Rewrite(source, sourceDoc, targetDoc));
        Assert.Equal("id-beyond-32-bits", ex.Kind);
    }

    [Theory]
    [InlineData("truncated")]
    [InlineData("unordered")]
    [InlineData("wrong class id")]
    public void MalformedSourceTable_IsRejected(string mode)
    {
        var sourceDoc = TfSchemaReader.Parse(ElemTableSchema(wideIds: true))!;
        var targetDoc = TfSchemaReader.Parse(ElemTableSchema(wideIds: false))!;
        byte[] source = WideRows((10, 10, 0, 0, -1), (20, 20, 0, 0, -1));
        if (mode == "truncated") source = source[..(6 + 40 + 39)];
        if (mode == "unordered") BitConverter.GetBytes(5L).CopyTo(source, 6 + 40 + 20);
        if (mode == "wrong class id") source[0] = 20;

        Assert.Throws<InvalidDataException>(() => DowngradeElemTableRewriter.Rewrite(source, sourceDoc, targetDoc));
    }
}
