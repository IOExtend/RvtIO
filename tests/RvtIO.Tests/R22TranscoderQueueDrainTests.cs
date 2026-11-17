using System.Buffers.Binary;
using RvtIO.Parsing;
using Xunit;
using Cls = RvtIO.Tests.SchemaStreamEditorTests.Cls;
using Fld = RvtIO.Tests.SchemaStreamEditorTests.Fld;
using NoRef = RvtIO.Tests.SchemaStreamEditorTests.NoRef;

namespace RvtIO.Tests;

/// <summary>A trailing default field value appended to one queued object must survive the cut
/// of the queued object that follows it: the cut discards only the edits strictly inside
/// its own byte range, never a zero-width insertion exactly at its start.</summary>
public sealed class R22TranscoderQueueDrainTests
{
    private static SchemaDocument Doc(params Cls[] classes) =>
        TfSchemaReader.Parse(SchemaStreamEditorTests.Write([.. classes]).Data);

    // Source: R { a: i32, pv: pointer to V, p: pointer to X }, V { d: i32 }, X { x: i32 }.
    // Target: R { a, pv, p }, V { d, e: i32 (missing in source, written empty) }, no X.
    private static readonly Cls[] SourceModel =
    [
        new("R", new NoRef(), 1, [new Fld("a", 0x04), new Fld("pv", 0x0E, 1), new Fld("p", 0x0E, 1)], []),
        new("V", new NoRef(), 1, [new Fld("d", 0x04)], []),
        new("X", new NoRef(), 1, [new Fld("x", 0x04)], []),
    ];

    private static readonly Cls[] TargetModel =
    [
        new("R", new NoRef(), 1, [new Fld("a", 0x04), new Fld("pv", 0x0E, 1), new Fld("p", 0x0E, 1)], []),
        new("V", new NoRef(), 1, [new Fld("d", 0x04), new Fld("e", 0x04)], []),
    ];

    private static byte[] U32(uint v) { var b = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); return b; }
    private static byte[] I32(int v) { var b = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(b, v); return b; }

    [Fact]
    public void NoPointer_DefaultAppended()
    {
        // No queue at all: R { a } -> R { a, e }. Pins down basic Default insertion.
        var source = Doc(
            new Cls("R", new NoRef(), 1, [new Fld("a", 0x04)], []));
        var target = Doc(
            new Cls("R", new NoRef(), 1, [new Fld("a", 0x04), new Fld("e", 0x04)], []));
        var transcoder = new R22Transcoder(source, target, sourceIdWidth: 4);
        byte[] full = [12, 0, 7, 0, 0, 0];
        byte[]? converted = transcoder.Payload(full, 0, full.Length, out string? failure);
        Assert.True(converted != null, "Payload failed: " + failure);
        Assert.Equal(new byte[] { 12, 0, 7, 0, 0, 0, 0, 0, 0, 0 }, converted);
    }

    [Fact]
    public void DroppedQueuedObjectAlone_Transcodes()
    {
        // R { a, p->X }, X missing in target: p nulled, X payload cut, no defaults involved.
        var source = Doc(
            new Cls("R", new NoRef(), 1, [new Fld("a", 0x04), new Fld("p", 0x0E, 1)], []),
            new Cls("X", new NoRef(), 1, [new Fld("x", 0x04)], []));
        var target = Doc(
            new Cls("R", new NoRef(), 1, [new Fld("a", 0x04), new Fld("p", 0x0E, 1)], []));
        var transcoder = new R22Transcoder(source, target, sourceIdWidth: 4);
        var root = new byte[2 + 4 + 4 + 2];
        BinaryPrimitives.WriteUInt16LittleEndian(root.AsSpan(0, 2), 12);
        BinaryPrimitives.WriteInt32LittleEndian(root.AsSpan(2, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(root.AsSpan(6, 4), 45);
        BinaryPrimitives.WriteUInt16LittleEndian(root.AsSpan(10, 2), 13);
        byte[] full = [.. root, .. I32(3)];
        byte[]? converted = transcoder.Payload(full, 0, full.Length, out string? failure);
        Assert.True(converted != null, "Payload failed: " + failure);
        var expected = new MemoryStream();
        void Tag(int id) { var t = new byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(t, (ushort)id); expected.Write(t); }
        Tag(12); expected.Write(I32(1)); expected.Write(new byte[4]);
        Assert.Equal(expected.ToArray(), converted);
    }

    [Fact]
    public void PointerQueueAlone_Transcodes()
    {
        // Sanity: R { a, pv->V }, V { d } round-trips with tag remap and no edits.
        var source = Doc(
            new("R", new NoRef(), 1, [new Fld("a", 0x04), new Fld("pv", 0x0E, 1)], []),
            new("V", new NoRef(), 1, [new Fld("d", 0x04)], []));
        var target = Doc(
            new("R", new NoRef(), 1, [new Fld("a", 0x04), new Fld("pv", 0x0E, 1)], []),
            new("V", new NoRef(), 1, [new Fld("d", 0x04)], []));
        var transcoder = new R22Transcoder(source, target, sourceIdWidth: 4);
        var root = new byte[2 + 4 + 4 + 2];
        BinaryPrimitives.WriteUInt16LittleEndian(root.AsSpan(0, 2), 12);
        BinaryPrimitives.WriteInt32LittleEndian(root.AsSpan(2, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(root.AsSpan(6, 4), 44);
        BinaryPrimitives.WriteUInt16LittleEndian(root.AsSpan(10, 2), 13);
        byte[] full = [.. root, .. I32(2)];
        byte[]? converted = transcoder.Payload(full, 0, full.Length, out string? failure);
        Assert.True(converted != null, "Payload failed: " + failure);
        Assert.Equal(2 + 4 + 4 + 2 + 4, converted.Length);
    }

    [Fact]
    public void TruncatedInputsFailAndFullObjectTranscodes()
    {
        var source = Doc(SourceModel);
        var target = Doc(TargetModel);
        var transcoder = new R22Transcoder(source, target, sourceIdWidth: 4);
        var root = new byte[2 + 4 + 4 + 2 + 4 + 2];
        BinaryPrimitives.WriteUInt16LittleEndian(root.AsSpan(0, 2), 12);
        BinaryPrimitives.WriteInt32LittleEndian(root.AsSpan(2, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(root.AsSpan(6, 4), 44);
        BinaryPrimitives.WriteUInt16LittleEndian(root.AsSpan(10, 2), 13);
        BinaryPrimitives.WriteUInt32LittleEndian(root.AsSpan(12, 4), 45);
        BinaryPrimitives.WriteUInt16LittleEndian(root.AsSpan(16, 2), 14);
        var queuedV = I32(2);
        var queuedX = I32(3);
        byte[] full = [.. root, .. queuedV, .. queuedX];
        // Prefixes end inside the object: the walk stops, either short of the end or past it.
        foreach (int size in new[] { 6, 10, 12, 16, 18, 22 })
            Assert.Null(transcoder.Payload(full, 0, size, out _));
        // The whole object transcodes: dropped pointer nulled, queued V kept with its default tail, X cut.
        byte[]? converted = transcoder.Payload(full, 0, full.Length, out string? failure);
        Assert.True(converted != null, "Payload failed: " + failure);
        Assert.Equal(24, converted.Length);
    }

    [Fact]
    public void TrailingDefaultSurvivesTheCutOfTheNextQueuedObject()
    {
        var source = Doc(SourceModel);
        var target = Doc(TargetModel);
        // Class ids: source R=12 V=13 X=14, target R=12 V=13.
        var transcoder = new R22Transcoder(source, target, sourceIdWidth: 4);

        // Root R: a=1, pv=(handle 44)->V, p=(handle 45)->X; queued V { d=2 }, queued X { x=3 }.
        // Root inline bytes: tag R, a, pv-handle, pv-tag, p-handle, p-tag; queued payloads carry no leading tag.
        var root = new byte[2 + 4 + 4 + 2 + 4 + 2];
        BinaryPrimitives.WriteUInt16LittleEndian(root.AsSpan(0, 2), 12);
        BinaryPrimitives.WriteInt32LittleEndian(root.AsSpan(2, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(root.AsSpan(6, 4), 44);
        BinaryPrimitives.WriteUInt16LittleEndian(root.AsSpan(10, 2), 13);
        BinaryPrimitives.WriteUInt32LittleEndian(root.AsSpan(12, 4), 45);
        BinaryPrimitives.WriteUInt16LittleEndian(root.AsSpan(16, 2), 14);
        var queuedV = I32(2);
        var queuedX = I32(3);
        byte[] full = [.. root, .. queuedV, .. queuedX];

        // Payload() takes one object: feed the root object alone is not how the queue works,
        // so instead build a single object whose queued V is followed by a dropped X.
        // Root2 { a, pv->V } with V { d } + X dropped right after V:
        // use the same R: pv->V known (queued), p->X dropped (queued right after V's payload).
        byte[]? converted = transcoder.Payload(full, 0, full.Length, out string? failure);
        Assert.True(converted != null, "Payload failed: " + failure);

        // Expected: tag R'(12), a=1, pv-handle 44, pv-tag V'(13), p-handle 0 (nulled),
        // V payload d=2 plus the default e=0; the X payload is cut out.
        var expected = new MemoryStream();
        void Tag(int id) { var t = new byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(t, (ushort)id); expected.Write(t); }
        Tag(12); expected.Write(I32(1)); expected.Write(U32(44)); Tag(13); expected.Write(U32(0));
        expected.Write(I32(2)); expected.Write(I32(0));
        Assert.Equal(expected.ToArray(), converted);

        // And the result walks to its last byte under the target schema.
        var walker = new WideBlockWalker(target, (d, f) => d.Name + "." + f.Name, []);
        var walk = walker.Run(converted, 0, converted.Length, 12);
        Assert.True(walk.Exact, walk.Error + " @ " + walk.StopField);
    }
}
