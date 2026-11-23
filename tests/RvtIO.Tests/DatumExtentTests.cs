using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

public sealed class DatumExtentTests
{
    [Theory]
    [InlineData(3, false, -1, true)]
    [InlineData(3, true, -1, false)]
    [InlineData(3, false, 3, false)]
    [InlineData(1, false, -1, true)]
    public void ExtractViewDatumExtents_ReadsContinuousSectionTrace(
        int segmentCount, bool hideLine, double visibleLength, bool supported)
    {
        const int owner = 100, view = 200, section = 300;
        const ushort extentClass = 1471, sectionClass = 886;
        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer);
        buffer.Position = 0x2e;
        writer.Write(1);
        writer.Write(owner);
        foreach (int id in new[] { -1, -1, -1, view, -1, -1, -1 }) writer.Write(id);
        writer.Write(new byte[3]);
        foreach (double value in new double[] { 0, 0, -1 }) writer.Write(value);
        writer.Write(view);
        writer.Write(1);
        writer.Write(section);
        writer.Write(uint.MaxValue);
        writer.Write(sectionClass);
        writer.Write(segmentCount);
        writer.Write(visibleLength);
        writer.Write(-1.0);
        for (int i = 1; i < segmentCount; i++)
        {
            writer.Write(-1.0);
            writer.Write(-1.0);
        }
        double[] bounds = segmentCount == 1 ? [2, 42, 0, 10, 2, 0] : [-8, 42, 0, 10, -2, 32];
        foreach (double value in bounds) writer.Write(value);
        writer.Write(1);
        writer.Write(2);
        writer.Write(hideLine);
        uint size = (uint)buffer.Position - 12;
        writer.Write(size);
        buffer.Position = 0;
        writer.Write(owner);
        writer.Write(0);
        writer.Write(size);
        writer.Write(extentClass);

        var extent = TfBlockDeserializer.ExtractViewDatumExtents(buffer.ToArray(), 0, size, extentClass);
        if (supported)
        {
            Assert.NotNull(extent);
            Assert.Empty(extent.Datums);
            var expected = segmentCount == 1
                ? new TfBlockDeserializer.SectionTraceExtent(2, 42, 1)
                : new TfBlockDeserializer.SectionTraceExtent(-2, 32, 3);
            Assert.Equal(expected, extent.Sections![section]);
        }
        else Assert.Null(extent);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExtractViewDatumExtents_BindsDeferredLeaderToItsDatum(bool freeEnd)
    {
        const int owner = 100, view = 200, datum = 300;
        const ushort extentClass = 1471, datumClass = 1054, leaderClass = 622;
        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer);
        writer.BaseStream.Position = 0x2e;
        writer.Write(1); // project document handle
        writer.Write(owner);
        foreach (int id in new[] { -1, -1, -1, view, -1, -1, -1 }) writer.Write(id);
        writer.Write(new byte[3]); // element flags
        WriteDoubles(1, 0, 0);
        writer.Write(view);
        writer.Write(1); // extent map count
        writer.Write(datum);
        writer.Write(uint.MaxValue);
        writer.Write(datumClass);
        WriteDoubles(-10, 20, -12, 15, -1, -1);
        writer.Write(-1); // the extent-map owner supplies the view
        writer.Write((byte)0x30);
        if (freeEnd) writer.Write(0);
        writer.Write(uint.MaxValue);
        writer.Write(leaderClass);
        if (!freeEnd) writer.Write(0);
        WriteDoubles(3, -14, 5, 3, -17, 4, 3, -20, 4, 0.09, -0.01, 0.7);
        writer.Write(2);
        writer.Write(1);
        writer.Write(false);
        uint size = (uint)buffer.Position - 12;
        writer.Write(size);
        writer.BaseStream.Position = 0;
        writer.Write(owner);
        writer.Write(0);
        writer.Write(size);
        writer.Write(extentClass);
        byte[] block = buffer.ToArray();

        var result = TfBlockDeserializer.ExtractViewDatumExtents(block, 0, size, extentClass);

        Assert.NotNull(result);
        Assert.Equal(view, result.ViewId);
        var extent = Assert.Single(result.Datums);
        Assert.Equal(datum, extent.Key);
        Assert.Equal(-10, extent.Value.FreeEnd);
        Assert.Equal(20, extent.Value.BubbleEnd);
        var leader = freeEnd ? extent.Value.FreeEndLeader : extent.Value.BubbleLeader;
        Assert.Equal(new double[] { 3, -17, 4 }, leader?.Elbow);
        Assert.Null(freeEnd ? extent.Value.BubbleLeader : extent.Value.FreeEndLeader);
        block[^1] ^= 1;
        Assert.Null(TfBlockDeserializer.ExtractViewDatumExtents(block, 0, size, extentClass));

        void WriteDoubles(params double[] values)
        {
            foreach (double value in values) writer.Write(value);
        }
    }
}
