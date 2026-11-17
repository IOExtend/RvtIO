using System.Buffers.Binary;
using RvtIO.Writing;
using Xunit;

namespace RvtIO.Tests;

public sealed class ArchiveContentsTests
{
    private static byte[] Header(uint version, byte marker = 0)
    {
        var bytes = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, ArchiveContents.Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), version);
        bytes[8] = marker;
        return bytes;
    }

    [Fact]
    public void ForTarget_TakesTheProfileHeader_WhenItsVersionIsOlder()
    {
        byte[] source = Header(31, 1);
        byte[] profile = Header(26, 2);

        Assert.Same(profile, ArchiveContents.ForTarget(source, profile));
    }

    [Fact]
    public void ForTarget_KeepsTheSourceHeader_WhenTheProfileIsNotOlder()
    {
        byte[] source = Header(26, 1);
        byte[] profile = Header(27, 2);

        Assert.Same(source, ArchiveContents.ForTarget(source, profile));
    }

    [Fact]
    public void ForTarget_WithoutAProfileHeader_KeepsTheSourceHeader()
    {
        byte[] source = Header(31);
        Assert.Same(source, ArchiveContents.ForTarget(source, null));
        Assert.Null(ArchiveContents.ForTarget(null, Header(26)));
        Assert.Null(ArchiveContents.ForTarget([1, 2, 3], Header(26)));
    }

    [Fact]
    public void TryReadVersion_RejectsForeignBytes()
    {
        Assert.False(ArchiveContents.TryReadVersion([0x62, 0x19, 0x22, 0x06, 0, 0, 0, 26], out _));
        Assert.True(ArchiveContents.TryReadVersion(Header(28), out uint version));
        Assert.Equal(28u, version);
    }
}
