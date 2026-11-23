using System.IO.Compression;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

public class PagedGzipStreamTests
{
    private static byte[] Payload(int length)
    {
        var data = new byte[length];
        new Random(11).NextBytes(data);
        return data;
    }

    private static byte[] Member(byte[] payload)
    {
        using var stream = new MemoryStream();
        using (var gzip = new GZipStream(stream, CompressionLevel.NoCompression, leaveOpen: true)) gzip.Write(payload);
        return stream.ToArray();
    }

    /// <summary>[8-byte prefix][member] with the page segment of the first page boundary inserted and
    /// <paramref name="tail"/> bytes of padding (not a segment) after the member.</summary>
    private static byte[] StreamWithSegment(byte[] member, int tail)
    {
        byte[] body = [.. new byte[PagedGzipStream.PrefixLength], .. member];
        var raw = new List<byte>(body.Take(PagedGzipStream.PageDataLength));
        raw.AddRange(new byte[PagedGzipStream.SegmentLength]);
        raw.AddRange(body.Skip(PagedGzipStream.PageDataLength));
        raw.AddRange(Enumerable.Repeat((byte)0x30, tail));
        return [.. raw];
    }

    [Fact]
    public void MemberEndingBeforeTheNextSegmentPosition_DecodesWithPaddingOverThatPosition()
    {
        byte[] payload = Payload(125_000);
        byte[] raw = StreamWithSegment(Member(payload), tail: 6_000);
        int secondSegment = PagedGzipStream.PageDataLength + PagedGzipStream.PageStride;
        Assert.True(raw.Length > secondSegment);
        Assert.Null(PagedGzipStream.RemoveSegments(raw));
        Assert.Equal(payload, PagedGzipStream.Decompress(raw));
    }

    [Fact]
    public void MemberEndingBeforeTheMarkerOfAnExactLengthTerminalPageRequiresAValidTrailer()
    {
        byte[] payload = Payload(64_000);
        byte[] member = Member(payload);
        var raw = new byte[PagedGzipStream.PageStride];
        member.CopyTo(raw, PagedGzipStream.PrefixLength);
        raw[PagedGzipStream.PageDataLength] = 0x83;
        Assert.Equal(payload, PagedGzipStream.Decompress(raw));

        raw[PagedGzipStream.PrefixLength + member.Length - 1] ^= 1;
        Assert.Null(PagedGzipStream.Decompress(raw));
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(PagedGzipStream.SegmentLength - 1, false)]
    [InlineData(PagedGzipStream.SegmentLength, true)]
    [InlineData(PagedGzipStream.SegmentLength + 1, false)]
    [InlineData(6_000, false)]
    public void OnlyAnExactLengthTerminalPageIsTrimmedAtAMissingSegmentMarker(int remaining, bool trimmed)
    {
        byte[] payload = Payload(100_000);
        int secondSegment = PagedGzipStream.PageDataLength + PagedGzipStream.PageStride;
        var raw = new byte[secondSegment + remaining];
        StreamWithSegment(Member(payload), tail: 0).CopyTo(raw, 0);
        raw[secondSegment] = 0x83;

        Assert.Null(PagedGzipStream.RemoveSegments(raw));
        // The segment of the first boundary is removed in every case; the bytes behind the second position stay unless that
        // position is the start of an exact-length terminal page.
        var tolerant = PagedGzipStream.RemoveSegments(raw, tolerateTail: true);
        Assert.Equal(raw.Length - PagedGzipStream.SegmentLength - (trimmed ? PagedGzipStream.SegmentLength : 0), tolerant?.Length);
        Assert.Equal(payload, PagedGzipStream.Decompress(raw));
    }

    [Fact]
    public void AValidSegmentMarkerOfATerminalPageIsRemovedByTheStrictRule()
    {
        byte[] payload = Payload(100_000);
        int secondSegment = PagedGzipStream.PageDataLength + PagedGzipStream.PageStride;
        var raw = new byte[secondSegment + PagedGzipStream.SegmentLength];
        StreamWithSegment(Member(payload), tail: 0).CopyTo(raw, 0);

        Assert.Equal(raw.Length - 2 * PagedGzipStream.SegmentLength, PagedGzipStream.RemoveSegments(raw)?.Length);
        Assert.Equal(payload, PagedGzipStream.Decompress(raw));
    }

    [Fact]
    public void StreamWithoutItsFirstSegment_IsRejectedByTheMemberCrc()
    {
        byte[] raw = [.. new byte[PagedGzipStream.PrefixLength], .. Member(Payload(100_000))];
        Assert.Null(PagedGzipStream.Decompress(raw));
    }

    [Fact]
    public void MemberCutInsideItsSecondSegment_IsRejected()
    {
        byte[] raw = StreamWithSegment(Member(Payload(200_000)), tail: 0);
        byte[] cut = raw[..(PagedGzipStream.PageDataLength + PagedGzipStream.PageStride + 1)];
        Assert.Null(PagedGzipStream.Decompress(cut));
    }

    [Fact]
    public void MemberCutBeforeItsTrailer_IsRejected()
    {
        byte[] member = Member(Payload(1_000));
        Assert.Null(PagedGzipStream.Decompress(member[..^8]));
        Assert.NotNull(PagedGzipStream.Decompress(member));
    }

    [Fact]
    public void MemberWithAnExtraHeaderField_IsDecodedAndWhenCutIsRejected()
    {
        byte[] payload = Payload(200_000);
        byte[] member = Member(payload);
        byte[] extra = [1, 2, 3, 4, 5, 6, 7, 8];
        byte[] withExtra = [.. member[..3], (byte)(member[3] | 4), .. member[4..10], 8, 0, .. extra, .. member[10..]];
        byte[] raw = StreamWithSegment(withExtra, tail: 0);
        byte[] cut = raw[..(PagedGzipStream.PageDataLength + PagedGzipStream.PageStride + 1)];
        Assert.Null(PagedGzipStream.Decompress(cut));
        Assert.Equal(payload, PagedGzipStream.Decompress(raw));
    }

    private static byte[] WithHeaderFlags(byte[] member, byte flags, byte[] afterHeader) =>
        [.. member[..3], flags, .. member[4..10], .. afterHeader, .. member[10..]];

    [Fact]
    public void HeaderWithAWrongHeaderCrc_IsRejected()
    {
        byte[] member = Member(Payload(1_000));
        Assert.Null(PagedGzipStream.Decompress(WithHeaderFlags(member, 2, [0, 0])));
    }

    [Fact]
    public void HeaderWithTheRightHeaderCrc_IsDecoded()
    {
        byte[] payload = Payload(1_000);
        byte[] member = Member(payload);
        var crc = new ICSharpCode.SharpZipLib.Checksum.Crc32();
        crc.Update(member[..3].Concat(new byte[] { 2 }).Concat(member[4..10]).ToArray());
        Assert.Equal(payload, PagedGzipStream.Decompress(WithHeaderFlags(member, 2, BitConverter.GetBytes((ushort)crc.Value))));
    }

    [Fact]
    public void HeaderWithAReservedFlag_IsRejected()
    {
        Assert.Null(PagedGzipStream.Decompress(WithHeaderFlags(Member(Payload(1_000)), 0x20, [])));
    }
}
