using Xunit;

namespace RvtIO.Tests;

public class PageTailProbeTests
{
    // A single set payload bit in lane 2. The parity constants are independent polynomial remainders.
    [Theory]
    [InlineData(3, 65, 5, 0x7)]
    [InlineData(4, 65, 10, 0x5)]
    [InlineData(5, 65, 19, 0x18)]
    [InlineData(6, 65, 39, 0x36)]
    [InlineData(7, 65, 76, 0x2b)]
    [InlineData(9, 65, 167, 0x1e5)]
    [InlineData(11, 65, 637, 0x107)]
    [InlineData(7, 81, 127, 0x41)]
    [InlineData(11, 255, 2047, 0x201)]
    public void ChecksShortenedAndFullLaneParity(int degree, int lanes, int rows, int parity)
    {
        var bytes = new byte[(rows * lanes + 7) / 8];
        bytes[0] = 4;
        for (int i = 0; i < degree; i++)
        {
            int bit = (rows - degree + i) * lanes + 2;
            if ((parity & (1 << i)) != 0) bytes[bit >> 3] |= (byte)(1 << (bit & 7));
        }
        bool Matches(PageTailProbe.Layout l) => l.Degree == degree && l.Lanes == lanes && l.Rows == rows;
        Assert.Contains(PageTailProbe.Inspect(bytes), Matches);
        bytes[0] ^= 4;
        Assert.DoesNotContain(PageTailProbe.Inspect(bytes), Matches);
    }

    // 45 bytes of (7*i + 3) mod 256, three padding bytes and a four-bit count, independently encoded.
    private static byte[] CountedPage() => Convert.FromHexString(
        "030a11181f262d343b424950575e656c737a81888f969da4abb2b9c0c7ced5dce3eaf1f8ff060d141b22293037" +
        "0000008cb2988900993ea469f344de574e63743e21033fbcd2934a0c226d0be713952b7e02");

    [Fact]
    public void ReadsCountAndRejectsSingleBitChanges()
    {
        byte[] page = CountedPage();
        Assert.Equal(new PageTailProbe.Layout(4, 65, 10, 4, 3, 45), Assert.Single(PageTailProbe.Inspect(page)));
        foreach (int bit in new[] { 0, 360, 386, 390, 649, 650 })
        {
            byte[] changed = (byte[])page.Clone();
            changed[bit >> 3] ^= (byte)(1 << (bit & 7));
            Assert.Empty(PageTailProbe.Inspect(changed));
        }
    }

    [Fact]
    public void ReportsAmbiguousZeroPagesAndRejectsOutOfRangeLengths()
    {
        Assert.True(PageTailProbe.Inspect(new byte[82]).Count > 1);
        Assert.Empty(PageTailProbe.Inspect([]));
        Assert.Empty(PageTailProbe.Inspect(new byte[65_250]));
    }

    [Fact]
    public void DistinguishesValidParityFromAValidPaddingCount()
    {
        byte[] nonzeroGap = CountedPage();
        // Change one gap bit and its two parity bits, preserving the codeword.
        foreach (int bit in new[] { 360, 555, 620 }) nonzeroGap[bit >> 3] ^= (byte)(1 << (bit & 7));
        Assert.Null(Assert.Single(PageTailProbe.Inspect(nonzeroGap)).ContentBytesIfCounted);

        byte[] zeroCount = CountedPage();
        // Clear both set bits of the count and adjust their parity. The payload still ends at byte 45.
        foreach (int bit in new[] { 386, 581, 646, 387, 582, 647 }) zeroCount[bit >> 3] ^= (byte)(1 << (bit & 7));
        var layout = Assert.Single(PageTailProbe.Inspect(zeroCount));
        Assert.Equal(0, layout.PaddingCount);
        Assert.Equal(48, layout.ContentBytesIfCounted);
    }
}
