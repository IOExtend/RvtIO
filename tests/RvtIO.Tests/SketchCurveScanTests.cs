using RvtIO.Model;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

public class SketchCurveScanTests
{
    [Fact]
    public void UnalignedLineAfterInvalidHeaderRespectsBlockEnd()
    {
        byte[] data = new byte[160];
        BitConverter.GetBytes(9).CopyTo(data, 3); // Unsupported curve tag.
        BitConverter.GetBytes(0x01080004u).CopyTo(data, 15);
        const int line = 39;
        BitConverter.GetBytes(41).CopyTo(data, line + 8);
        BitConverter.GetBytes(0x01080004u).CopyTo(data, line + 12);
        BitConverter.GetBytes(3.0).CopyTo(data, line + 24); // End parameter.
        BitConverter.GetBytes(1.0).CopyTo(data, line + 56); // X direction.
        var curves = new Dictionary<uint, LocationCurve>();
        var styles = new Dictionary<uint, int>();
        BlockParser.ExtractSketchCurve(data, 3, line + 79, 123, curves, styles);
        Assert.Empty(curves);
        Assert.Empty(styles);
        BlockParser.ExtractSketchCurve(data, 3, line + 80, 123, curves, styles);
        Assert.Equal(3.0, curves[123].Length);
        Assert.Equal(41, styles[123]);
    }
}
