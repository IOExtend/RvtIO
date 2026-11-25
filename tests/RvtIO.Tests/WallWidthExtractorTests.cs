using RvtIO.Internal;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

public class WallWidthExtractorTests
{
    [Fact]
    public void InheritedDoublesShareTheFilteredSnapshotAndKeepWallWidthsLocal()
    {
        var state = new ParsingState();
        state.ElemBipDoubles[10] = new() { [20] = 42, [-1001105] = 8 };
        state.ElemBipDoubles[11] = new() { [-1001105] = 9, [-1001001] = 6 };
        state.WallTypeWidths[12] = 0.75;
        state.Records[20] = new ElementRecord { References = [10, 12], CategoryId = -2000011 };
        state.Records[21] = new ElementRecord { References = [20] };
        state.Records[22] = new ElementRecord { References = [10] };
        state.Records[23] = new ElementRecord { References = [10, 11] };

        RvtFileReader.PrePropagateBipDoubles(state, null);

        var inherited = state.ElemBipDoubles[21];
        Assert.Single(inherited);
        Assert.Equal(8.0, inherited[-1001105]);
        Assert.Same(inherited, state.ElemBipDoubles[22]);
        Assert.Equal(0.75, state.ElemBipDoubles[20][-1001000]);
        Assert.Equal(8.0, state.ElemBipDoubles[23][-1001105]);
        Assert.Equal(6.0, state.ElemBipDoubles[23][-1001001]);
    }

    [Fact]
    public void Extract_KeepsFirstOrderedBlockGridBeforeRecordFallback()
    {
        var state = new ParsingState();
        state.Records[420] = new ElementRecord { CategoryId = -2000011, Body = Grid(1.5) };
        state.Records[421] = new ElementRecord { CategoryId = -2000011, Body = Grid(2) };
        state.Records[422] = new ElementRecord { CategoryId = -2000011, Body = Grid(3) };
        state.ElemBipDoubles[422] = new() { [-1001105] = 9 };
        state.NonRecordChunks.Add(Block(420, 0.5));
        state.RecordChunks.Add(Block(420, 1));

        WallWidthExtractor.Extract(state);

        Assert.Equal(0.5, state.WallTypeWidths[420]);
        Assert.Equal(0.5, state.ElemBipDoubles[420][-1001000]);
        Assert.Equal(2.0, state.WallTypeWidths[421]);
        Assert.False(state.WallTypeWidths.ContainsKey(422));
    }

    private static byte[] Grid(double width)
    {
        var grid = new byte[48];
        BitConverter.GetBytes(width).CopyTo(grid, 24);
        return grid;
    }

    private static byte[] Block(uint id, double width)
    {
        var block = new byte[64];
        BitConverter.GetBytes(id).CopyTo(block, 0);
        BitConverter.GetBytes(48u).CopyTo(block, 8);
        Grid(width).CopyTo(block, 16);
        return block;
    }
}
