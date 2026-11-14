using System.Text;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

/// <summary>The rewrite of a later release's partition stream into the Revit 2022 layout (<see cref="R22Transcoder"/>): a hand-made block
/// with a renumbered class, a 64-bit id, a field only the file has, a field only the 2022 class has and an object of a class the 2022
/// table does not know, then Test_2025.rvt read with the rewrite.</summary>
public class R22TranscoderTests
{
    private static SchemaField Field(string name, byte type, byte mods = 0, int? reference = null) =>
        new(name, type, mods, 0, null, reference, null, null);

    // the file: Holder(5) is renumbered to 11, Store(3) to 10 and Extra(4) has no counterpart
    private static SchemaDocument Source() => new()
    {
        Classes =
        [
            new(1, "Identifier", 0, 2, [Field("m_id64", 11)], []),
            new(2, "ElementId", 0, 1, [Field("m_id", 14, 0, 1)], []),
            new(3, "Store", 0, 1, [Field("m_value", 4)], []),
            new(4, "Extra", 0, 1, [Field("m_x", 4)], []),
            new(5, "Holder", 0, 1,
                [Field("m_pStore", 14, 1), Field("m_items", 14, 0x51), Field("m_id", 14, 0, 2), Field("m_added", 4), Field("m_name", 8, 96)], []),
            new(6, "Pair", 0, 1, [Field("m_first", 4), Field("m_second", 4), Field("m_id", 14, 0, 2)], []),
        ],
    };

    private static SchemaDocument Target() => new()
    {
        Classes =
        [
            new(1, "Identifier", 0, 1, [Field("m_id", 4)], []),
            new(2, "ElementId", 0, 1, [Field("m_id", 14, 0, 1)], []),
            new(10, "Store", 0, 1, [Field("m_value", 4)], []),
            new(11, "Holder", 0, 1,
                [Field("m_pStore", 14, 1), Field("m_items", 14, 0x51), Field("m_id", 14, 0, 2), Field("m_gone", 4), Field("m_name", 8, 96)], []),
            new(12, "Pair", 0, 1, [Field("m_id", 14, 0, 2), Field("m_second", 4), Field("m_first", 4)], []),
        ],
    };

    private static byte[] Bytes(Action<BinaryWriter> body)
    {
        var stream = new MemoryStream();
        body(new BinaryWriter(stream, Encoding.Unicode));
        return stream.ToArray();
    }

    private static void Owned(BinaryWriter w, int tag) { w.Write(0xFFFFFFFFu); w.Write((ushort)tag); }

    [Fact]
    public void Payload_RenumbersCutsInsertsAndLeavesOutUnknownObjects()
    {
        var block = Bytes(w =>
        {
            w.Write((ushort)5);
            Owned(w, 3);                                   // m_pStore
            w.Write(2u); Owned(w, 4); Owned(w, 3);         // m_items: an Extra and a Store
            w.Write(77L);                                  // m_id (64 bits)
            w.Write(5);                                    // m_added
            w.Write(2u); w.Write(Encoding.Unicode.GetBytes("ab"));
            w.Write(42);                                   // Store
            w.Write(9);                                    // Extra
            w.Write(43);                                   // Store
        });
        var expected = Bytes(w =>
        {
            w.Write((ushort)11);
            Owned(w, 10);
            w.Write(1u); Owned(w, 10);                     // the Extra left the array, the count follows
            w.Write(77);                                   // m_id (32 bits)
            w.Write(0);                                    // m_gone, empty
            w.Write(2u); w.Write(Encoding.Unicode.GetBytes("ab"));
            w.Write(42);
            w.Write(43);
        });
        var transcoder = new R22Transcoder(Source(), Target(), 8);
        var result = transcoder.Payload(block, 0, block.Length);
        Assert.Equal(expected, result);
        var walk = new WideBlockWalker(Target(), (_, _) => null, []).Run(result!, 0, result!.Length, 11);
        Assert.True(walk.Exact, walk.Error);
    }

    [Fact]
    public void Payload_WritesFieldsInTheOrderOfThe2022Class()
    {
        var block = Bytes(w => { w.Write((ushort)6); w.Write(1); w.Write(2); w.Write(77L); });
        var expected = Bytes(w => { w.Write((ushort)12); w.Write(77); w.Write(2); w.Write(1); });
        Assert.Equal(expected, new R22Transcoder(Source(), Target(), 8).Payload(block, 0, block.Length));
    }

    [Fact]
    public void Payload_BytesAfterTheObjectAreNotRewritten_AndSayWhy()
    {
        var block = Bytes(w => { w.Write((ushort)6); w.Write(1); w.Write(2); w.Write(77L); w.Write((ushort)0); });
        Assert.Null(new R22Transcoder(Source(), Target(), 8).Payload(block, 0, block.Length, out var failure));
        Assert.Equal("Pair: bytes after the end of the object", failure);
    }

    [Fact]
    public void Payload_RootClassWithoutCounterpartIsNotRewritten()
    {
        var block = Bytes(w => { w.Write((ushort)4); w.Write(1); });
        Assert.Null(new R22Transcoder(Source(), Target(), 8).Payload(block, 0, block.Length));
    }

    [Fact]
    public void Payload_IdBeyond32BitsIsNotRewritten()
    {
        var block = Bytes(w =>
        {
            w.Write((ushort)5); w.Write(0u); w.Write(0u);
            w.Write(1L << 40); w.Write(5); w.Write(0u);
        });
        Assert.Null(new R22Transcoder(Source(), Target(), 8).Payload(block, 0, block.Length));
    }

    [Fact]
    public void Framed_RewritesTheBlocksOfTheProjectBlock_EachWholeInOneSegment_AndLeavesOutTheHeaders()
    {
        var payload = Bytes(w => { w.Write((ushort)6); w.Write(1); w.Write(2); w.Write(77L); });
        var unknown = Bytes(w => { w.Write((ushort)4); w.Write(1); });
        // [owner:8][channel != 101: checksum:4][size:4][payload][size:4]
        byte[] Item(ulong owner, bool header, byte[] body) => Bytes(w =>
        {
            w.Write(owner);
            if (!header) w.Write(0u);
            w.Write((uint)body.Length); w.Write(body); w.Write((uint)body.Length);
        });
        byte[] header = Item(123, true, payload), root = Item(123, false, payload), geometry = Item(456, false, unknown);
        byte[] stream = [.. header, .. root, .. geometry];
        int cut = header.Length + 10; // the root runs into the next segment
        var source = new PartitionFraming.Framed([stream[..cut], stream[cut..]],
            [new(101, 123, 0, header.Length), new(102, 123, header.Length, root.Length), new(103, 456, header.Length + root.Length, geometry.Length)], 8);
        var transcoder = new R22Transcoder(Source(), Target(), 8);

        var framed = transcoder.Framed(source, out var stats)!;

        Assert.Equal(4, framed.IdWidth);
        Assert.Equal([(102, 123UL), (103, 456UL)], framed.Items.Select(i => (i.Channel, i.Owner)));
        Assert.Equal((1, 1, 1, 0), (stats.RootBlocks, stats.RootBlocksRewritten, stats.OtherBlocks, stats.OtherBlocksRewritten));
        byte[] Block(uint owner, byte[] body) => Bytes(w =>
        {
            w.Write(owner); w.Write(Adler32(body));
            w.Write((uint)body.Length); w.Write(body); w.Write((uint)body.Length);
        });
        byte[] marked = [.. unknown];
        marked[0] = 0xFE; marked[1] = 0xFF; // R22Transcoder.UnknownTag
        byte[][] expected = [Block(123, transcoder.Payload(payload, 0, payload.Length)!), Block(456, marked)];
        for (int i = 0; i < 2; i++)
        {
            var (data, start, copied) = framed.Bytes(framed.Items[i]);
            Assert.False(copied);
            Assert.Equal(expected[i], data.AsSpan(start, framed.Items[i].Length).ToArray());
        }
        // a frame of another id width than the file's schema is not rewritten
        Assert.Null(transcoder.Framed(source with { IdWidth = 4 }, out _));
    }

    private static uint Adler32(byte[] data)
    {
        uint a = 1, b = 0;
        foreach (byte x in data) { a = (a + x) % 65521; b = (b + a) % 65521; }
        return (b << 16) | a;
    }

    [SkippableFact]
    public void Test2025_ReadWithTheRewriteSeesBlocksAndGeometryOfTheRevit2022Readers()
    {
        var diag = new StringWriter();
        var reader = new RvtFileReader(new RvtReaderOptions { R22Layout = true, DiagnosticOutput = diag });
        var info = reader.Read(TestFileFixture.FindTestFile("Test_2025.rvt"));
        var state = reader.LastParsingState!;
        Assert.True(state.R22View);
        Assert.Contains("Revit 2022 layout:", diag.ToString());
        Assert.NotEmpty(state.GrepBlocks);
        Assert.True(state.Records.Count > state.ElemTable.Count / 2);
        Assert.Contains(info.Elements, e => e.Geometry?.Faces.Count > 0);
    }

    [Fact]
    public void DriveCurve_OnlyTheCurveKeysOfTheRewrittenBlockAreAddedAndNothingIsOverwritten()
    {
        var target = new TfBlockDeserializer.NativeParams();
        target.DoubleFields["87:m_pCrv.endParams@1"] = 5.0;
        target.DoubleFields["1709:m_rotation"] = 0.25;
        var tail = new TfBlockDeserializer.NativeParams();
        tail.DoubleFields["87:m_pCrv.dirVec@0"] = 1.0;
        tail.DoubleFields["87:m_pCrv.endParams@1"] = 9.0;
        tail.DoubleFields["1709:m_rotation"] = 3.0;
        RvtFileReader.AddDriveCurveFromRewrittenBlock(target, tail);
        Assert.Equal(1.0, target.DoubleFields["87:m_pCrv.dirVec@0"]);
        Assert.Equal(5.0, target.DoubleFields["87:m_pCrv.endParams@1"]);
        Assert.Equal(0.25, target.DoubleFields["1709:m_rotation"]);
        RvtFileReader.AddDriveCurveFromRewrittenBlock(target, null);
        Assert.Equal(3, target.DoubleFields.Count);
    }

    [SkippableFact]
    public void Test2025_WithoutTheRewriteTheSchemaPathAloneApplies()
    {
        var reader = new RvtFileReader(new RvtReaderOptions());
        reader.Read(TestFileFixture.FindTestFile("Test_2025.rvt"));
        Assert.False(reader.LastParsingState!.R22View);
    }

    [Fact]
    public void Stream_SkipsAllPrunedRecordlessSectionAndRewritesFollowingSection()
    {
        var recordPayload = Bytes(w => { w.Write((ushort)6); w.Write(1); w.Write(2); w.Write(77L); });
        var stream = Bytes(w =>
        {
            // All-pruned section: zero records, zero root blocks, zero other blocks.
            w.Write(0xFFFFFFFFFFFFFFFFuL);
            w.Write(0L);
            w.Write(0xFFFFFFFFFFFFFFFFuL);
            w.Write(0);
            w.Write(0L);
            w.Write(0xFFFFFFFFFFFFFFFFuL);
            w.Write(0);
            w.Write(0L);

            // Second section with a normal 8-byte-id record.
            w.Write(10L);
            w.Write(recordPayload.Length);
            w.Write(recordPayload);
            w.Write(recordPayload.Length);
            w.Write(0xFFFFFFFFFFFFFFFFuL);
            w.Write(0L);
            w.Write(0xFFFFFFFFFFFFFFFFuL);
            w.Write(0);
            w.Write(0L);
            w.Write(0xFFFFFFFFFFFFFFFFuL);
            w.Write(0);
            w.Write(0L);
        });

        var transcoder = new R22Transcoder(Source(), Target(), 8);
        _ = transcoder.Stream([stream], out var stats);

        Assert.Equal(0, stats.UnparsedBytes);
        Assert.Equal(stream.Length, stats.StoppedAt);
        Assert.Equal(1, stats.Records);
        Assert.Equal(1, stats.RecordsRewritten);
        Assert.Empty(stats.Failures);
    }
}
