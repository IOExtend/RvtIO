using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

public class WallCompleteInputTests
{
    [Theory]
    [InlineData(3524)] [InlineData(361)]
    public void FullFooterAndEveryLateFieldAreRetainedWithoutChangingThePhasePrefix(int cls)
    {
        byte[] payload = Bytes(w => { w.Write(-1); w.Write(int.MinValue); });
        byte[] bytes = Block(2075, payload, root: (ushort)cls);
        var before = TfBlockDeserializer.ExtractOwnWallSixPhaseGeometryHistories(bytes, 0,
            BitConverter.ToUInt32(bytes, 8), (ushort)cls, out var oldFailure)!;
        var value = Read(bytes, out var failure)!;
        Assert.Null(oldFailure); Assert.Null(failure);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(before),
            System.Text.Json.JsonSerializer.Serialize(value.Histories));
        Assert.Equal(bytes, value.FrameBytes); Assert.Equal(bytes.Length - 4, value.End);
        var node = Assert.Single(value.LaterObjects);
        Assert.Equal(2, node.QueueIndex); Assert.Equal(uint.MaxValue, node.Handle);
        Assert.Equal(2075, node.ClassId); Assert.Equal(8, node.End - node.Start);
        Assert.Equal(new[] { "m_geomTag", "m_materialId" }, node.Fields.Select(f => f.Name));
        Assert.All(node.Fields, f => { Assert.Equal(4, f.Bytes.Length); Assert.Empty(f.QueuedObjects); });
        Assert.Equal(payload, node.Fields.SelectMany(f => f.Bytes).ToArray());
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void GElementBoxesAreTwoXYZValuesAndOwnedQueueOrderIsRetained(int children)
    {
        byte[] payload = Bytes(w =>
        {
            w.Write(new byte[16]); w.Write((uint)children);
            for (int i = 0; i < children; i++) { w.Write(42u + (uint)i); w.Write((ushort)2503); }
            for (int i = 0; i < 12; i++) w.Write(long.MinValue + i);
            w.Write(-1); w.Write(uint.MaxValue);
            for (int i = 0; i < children; i++) { w.Write(long.MaxValue - i); w.Write(int.MinValue); w.Write((byte)255); }
        });
        var value = Read(Block(2007, payload), out var failure)!; Assert.Null(failure);
        Assert.Equal(children + 1, value.LaterObjects.Count);
        var root = value.LaterObjects[0]; Assert.Equal(2007, root.ClassId);
        Assert.Equal(16, root.Fields.Single(f => f.Name == "m_GInfo").Bytes.Length);
        Assert.Equal(48, root.Fields.Single(f => f.Name == "m_bBox").Bytes.Length);
        Assert.Equal(48, root.Fields.Single(f => f.Name == "m_tightbBox").Bytes.Length);
        var array = root.Fields.Single(f => f.Name == "m_subNodes");
        Assert.Equal(Enumerable.Range(3, children), array.QueuedObjects);
        Assert.Equal(4 + 6 * children, array.End - array.Start);
        for (int i = 0; i < children; i++)
        {
            var node = value.LaterObjects[i + 1]; Assert.Equal(3 + i, node.QueueIndex);
            Assert.Equal(42u + (uint)i, node.Handle); Assert.Equal(13, node.End - node.Start);
        }
        Assert.All(value.LaterObjects.SelectMany(n => n.Fields), f =>
            Assert.Equal(f.End - f.Start, f.Bytes.Length));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(3)]
    public void EdgeUVValuesUseTwoCoordinatesPerPointWithoutNativePadding(int count)
    {
        byte[] payload = Bytes(w =>
        {
            w.Write(new byte[40]); w.Write((uint)count); w.Write(new byte[32 * count]);
            w.Write(new byte[64]); w.Write((byte)255);
        });
        var value = Read(Block(1243, payload), out var failure)!; Assert.Null(failure);
        var fields = Assert.Single(value.LaterObjects).Fields;
        Assert.Equal(4 + count * 32, fields.Single(f => f.Name == "m_interiorEdgePnts").Bytes.Length);
        Assert.Equal(64, fields.Single(f => f.Name == "m_firstAndLastEdgePnts").Bytes.Length);
        Assert.All(fields, f => Assert.Empty(f.QueuedObjects));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(8192)]
    public void ExactJoinContractsDoNotUseTheLegacy4096Limit(int count)
    {
        var value = Read(Block(89, Bytes(w =>
        {
            w.Write(1u); w.Write(int.MinValue); w.Write(int.MaxValue);
            w.Write((uint)count);
            for (int i = 0; i < count; i++) { w.Write(-1); w.Write(int.MinValue); w.Write((byte)255); }
            w.Write(int.MaxValue);
        })), out var failure)!;
        Assert.Null(failure); var fields = Assert.Single(value.LaterObjects).Fields;
        Assert.Equal(12, fields[0].Bytes.Length); Assert.Equal(4 + 9 * count, fields[1].Bytes.Length);
        Assert.All(fields, f => Assert.Empty(f.QueuedObjects));
    }

    [Fact]
    public void NullCountSentinelsRemainExactBytesInsteadOfEmptyCollections()
    {
        var value = Read(Block(89, Bytes(w =>
        { w.Write(uint.MaxValue); w.Write(uint.MaxValue); w.Write(-1); })), out var failure)!;
        Assert.Null(failure); var fields = Assert.Single(value.LaterObjects).Fields;
        Assert.Equal(BitConverter.GetBytes(uint.MaxValue), fields[0].Bytes);
        Assert.Equal(BitConverter.GetBytes(uint.MaxValue), fields[1].Bytes);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(255)]
    public void PlaneEnvelopeAndOrientationAreNotAcceptedByLookingForPlausibleCoordinates(int orientation)
    {
        var value = Read(Block(522, Bytes(w =>
        { w.Write(new byte[32]); w.Write((byte)orientation); for (int i = 0; i < 9; i++) w.Write(long.MaxValue); })), out var failure)!;
        Assert.Null(failure); var fields = Assert.Single(value.LaterObjects).Fields;
        Assert.Equal(32, fields[0].Bytes.Length); Assert.Equal(new byte[] { (byte)orientation }, fields[1].Bytes);
        Assert.Equal(105, fields.Sum(f => f.Bytes.Length));
    }

    [Theory]
    [InlineData(2075, 8)] [InlineData(2007, 124)] [InlineData(522, 105)]
    public void EveryLatePayloadTruncationFailsWithAValidReframedFooter(int cls, int width)
    {
        byte[] payload = new byte[width];
        for (int length = 0; length < width; length++)
        {
            Assert.Null(Read(Block((ushort)cls, payload[..length]), out var failure));
            Assert.StartsWith("late:", failure);
        }
        Assert.NotNull(Read(Block((ushort)cls, payload), out _));
    }

    [Theory]
    [InlineData(2075, 8, false)]
    [InlineData(1736, 80, true)]
    public void ReaderInputIsReusedAndOnlyRetainsConsumedLateObjects(int cls, int width, bool retained)
    {
        byte[] block = Block((ushort)cls, new byte[width]);
        var full = Read(block, out _)!;
        var reader = new TfBlockDeserializer.WallCompleteInputRead(block, 0, BitConverter.ToUInt32(block, 8), 3524);
        var selected = reader.Read(out var failure)!;

        Assert.Null(failure);
        Assert.Same(selected, reader.Read(out _));
        Assert.Equal(full.End, selected.End);
        Assert.Empty(selected.FrameBytes);
        if (retained)
            Assert.Equal(Assert.Single(full.LaterObjects).Fields.SelectMany(f => f.Bytes),
                Assert.Single(selected.LaterObjects).Fields.SelectMany(f => f.Bytes));
        else
            Assert.Empty(selected.LaterObjects);

        byte[] truncated = Block((ushort)cls, new byte[width - 1]);
        var invalid = new TfBlockDeserializer.WallCompleteInputRead(
            truncated, 0, BitConverter.ToUInt32(truncated, 8), 3524);
        Assert.Null(Read(truncated, out var expectedFailure));
        Assert.Null(invalid.Read(out failure));
        Assert.Equal(expectedFailure, failure);
        Assert.Null(invalid.Read(out failure));
        Assert.Equal(expectedFailure, failure);
    }

    [Theory]
    [InlineData(90)] [InlineData(901)] [InlineData(3634)]
    public void ExcessiveCountsFailRatherThanSkippingOrSeekingTheFooter(int cls)
    {
        Assert.Null(Read(Block((ushort)cls, BitConverter.GetBytes(1_000_001u)), out var failure));
        Assert.StartsWith("late:", failure);
    }

    [Theory]
    [InlineData(3905)] [InlineData(737)]
    public void UnsupportedLateRuntimeClassesAreExplicitFailures(int cls)
    {
        Assert.Null(Read(Block((ushort)cls, []), out var failure));
        Assert.Equal($"late:2:{cls}:schema", failure);
    }

    [Fact]
    public void TrailingUnownedBytesAreNotPromotedIntoACompleteResult()
    {
        Assert.Null(Read(Block(2075, new byte[9]), out var failure)); Assert.Equal("late-footer", failure);
    }

    [Fact]
    public void NonzeroFrameStartRetainsOnlyItsExactFrame()
    {
        byte[] bytes = Block(2075, new byte[8]), segment = new byte[bytes.Length + 23];
        bytes.CopyTo(segment, 11);
        var value = TfBlockDeserializer.ExtractOwnWallCompleteInputs(segment, 11,
            BitConverter.ToUInt32(bytes, 8), 3524, out var failure)!;
        Assert.Null(failure); Assert.Equal(bytes, value.FrameBytes);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(Read(bytes, out _)),
            System.Text.Json.JsonSerializer.Serialize(value));
        bytes[^1] ^= 1; Assert.Null(Read(bytes, out failure)); Assert.Equal("frame", failure);
    }

    [Fact]
    public void EmptyLaterFifoIsAnExactSerializedResultNotAnActiveGeometryDecision()
    {
        var (original, offsets) = WallGeometryPhaseTests.Block(3524);
        byte[] bytes = original[..(offsets["list-end"] + 4)];
        uint size = (uint)bytes.Length - 16;
        BitConverter.GetBytes(size).CopyTo(bytes, 8); BitConverter.GetBytes(size).CopyTo(bytes, bytes.Length - 4);
        var value = Read(bytes, out var failure)!;
        Assert.Null(failure); Assert.Empty(value.LaterObjects); Assert.Equal(bytes, value.FrameBytes);
        Assert.Equal(value.Histories.Histories.End, value.End);
    }

    [Fact]
    public void NullOwnedArrayCountIsRetainedWithoutAddingOrDroppingQueueEntries()
    {
        byte[] payload = new byte[124]; BitConverter.GetBytes(uint.MaxValue).CopyTo(payload, 16);
        var value = Read(Block(2007, payload), out var failure)!; Assert.Null(failure);
        var field = Assert.Single(value.LaterObjects).Fields.Single(f => f.Name == "m_subNodes");
        Assert.Equal(BitConverter.GetBytes(uint.MaxValue), field.Bytes); Assert.Empty(field.QueuedObjects);
    }

    [Theory]
    [InlineData(-1, 8u, 3524)] [InlineData(0, 0u, 3524)] [InlineData(0, uint.MaxValue, 3524)]
    [InlineData(0, 8u, 1794)] [InlineData(int.MaxValue, 8u, 3524)]
    public void InvalidFrameArgumentsDoNotThrow(int start, uint size, int cls)
    {
        Assert.Null(TfBlockDeserializer.ExtractOwnWallCompleteInputs(new byte[32], start, size, (ushort)cls, out var failure));
        Assert.Equal("frame", failure);
    }

    [Theory]
    [InlineData("owner")] [InlineData("null-document")] [InlineData("foreign-document")] [InlineData("null-list")]
    public void UnconfirmedRootOrDocumentContextDoesNotProduceACompleteResult(string mutation)
    {
        var (bytes, _) = WallGeometryPhaseTests.Block(3524, mutation);
        Assert.Null(Read(bytes, out var failure)); Assert.False(string.IsNullOrEmpty(failure));
    }

    private static TfBlockDeserializer.OwnWallCompleteInputs? Read(byte[] bytes, out string? failure)
        => TfBlockDeserializer.ExtractOwnWallCompleteInputs(bytes, 0, BitConverter.ToUInt32(bytes, 8),
            BitConverter.ToUInt16(bytes, 12), out failure);

    private static byte[] Block(ushort cls, byte[] payload, ushort root = 3524)
    {
        var (original, offsets) = WallGeometryPhaseTests.Block(root,
            geometryTablePointer: Bytes(w => { w.Write(uint.MaxValue); w.Write(cls); }));
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write(original.AsSpan(0, offsets["list-end"])); writer.Write(payload); writer.Write(0u);
        byte[] bytes = stream.ToArray(); uint size = (uint)bytes.Length - 16;
        BitConverter.GetBytes(size).CopyTo(bytes, 8); BitConverter.GetBytes(size).CopyTo(bytes, bytes.Length - 4);
        return bytes;
    }
    private static byte[] Bytes(Action<BinaryWriter> write)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        write(writer); return stream.ToArray();
    }
}
