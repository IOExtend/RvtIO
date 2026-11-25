using RvtIO.Internal;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

public class ElementValueStoreTests
{
    [Fact]
    public void PagedBuildMapsKeepTypesOrderingAndTextAcrossPages()
    {
        using var scratch = new MemoryStream();
        var state = new ParsingState
        {
            KeepExportProvenance = false,
            Chunks = new PartitionChunks(new PartitionChunks.Backing(scratch)),
        };
        int[] signedKeys = [-1001203, int.MinValue, 19];
        state.ElemBipInts[0] = new() { [signedKeys[0]] = -1, [signedKeys[1]] = 42, [signedKeys[2]] = 0 };
        state.ElemBipInts[123] = new();
        state.ElemBipDoubles[13] = new() { [-1001100] = -0.0, [19] = double.PositiveInfinity };
        const uint unsignedKey = uint.MaxValue;
        const string text = "Text\uD800";
        state.ElemParamStrings[1] = new() { [1] = new string('x', 70_000) };
        state.ElemParamStrings[2] = new() { [unsignedKey] = text, [2] = "" };

        state.ArchiveElementValues();

        Assert.Empty(state.ElemBipInts);
        Assert.Empty(state.ElemBipDoubles);
        Assert.Empty(state.ElemParamStrings);
        Assert.True(scratch.Length > 64 * 1024);
        Assert.True(state.TryReadBipInts(0, out var integers));
        Assert.Equal(signedKeys, integers.Keys);
        Assert.Equal([-1, 42, 0], integers.Values);
        Assert.True(state.TryReadBipInts(123, out var empty));
        Assert.Empty(empty);
        Assert.False(state.TryReadBipInts(31, out _));
        Assert.True(state.TryReadBipDoubles(13, out var doubles));
        Assert.Equal(BitConverter.DoubleToInt64Bits(-0.0), BitConverter.DoubleToInt64Bits(doubles[-1001100]));
        Assert.Equal(double.PositiveInfinity, doubles[19]);
        Assert.True(state.TryReadParamStrings(2, out var strings));
        Assert.Equal([unsignedKey, 2u], strings.Keys);
        Assert.Equal([text, ""], strings.Values);
        Assert.True(state.TryReadParamStrings(1, out var large));
        Assert.Equal(70_000, large[1].Length);
        Assert.True(state.TryReadParamStrings(2, out strings));
        Assert.Equal(text, strings[unsignedKey]);

        state.ReleaseElementBuildInputs();
        Assert.False(state.TryReadParamStrings(2, out _));
    }
}
