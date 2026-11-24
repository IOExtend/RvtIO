using RvtIO.Internal;
using RvtIO.Model;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

public class NativeParameterStoreTests
{
    [Fact]
    public void ScratchRoundTripPreservesOrderedValuesFlagsAndTypedEntities()
    {
        using var scratch = new MemoryStream();
        var store = new NativeParameterStore(new PartitionChunks.Backing(scratch));
        const string unpairedSurrogate = "\uD800";
        var parameter = new TfBlockDeserializer.NativeParams
        {
            RootParameterStoresComplete = true,
            TailWalkComplete = true,
            CellClasses = [17, 17, 42],
            RoofExtrusionFaceTags = [],
            ExtensibleStorage = [new ExtensibleStorageEntity(Guid.Empty, "schema", new Dictionary<string, object?>
                { ["value"] = 7 })],
            Doubles = { (1, -0.0), (1, double.NaN) },
            Ints = { (2, int.MinValue) },
            Strings = { (2, unpairedSurrogate), (2, "") },
            ElementIds = { (3, -1) },
            Fields = { ["count"] = 6 },
            DoubleFields = { ["length"] = double.PositiveInfinity },
            StringFields = { ["name"] = "Клапан" },
            NamedParams = { new(1, null, 1.5, -1, 3, true, false), new(2, "", -3, 2, 1, false, true) },
        };
        store[5] = parameter;
        store[7] = new TfBlockDeserializer.NativeParams();

        Assert.True(store.TryGetValue(5, out var restored));
        Assert.NotSame(parameter, restored);
        Assert.True(restored.RootParameterStoresComplete);
        Assert.True(restored.TailWalkComplete);
        Assert.Equal(parameter.CellClasses, restored.CellClasses);
        Assert.Empty(restored.RoofExtrusionFaceTags!);
        Assert.Same(parameter.ExtensibleStorage, restored.ExtensibleStorage);
        Assert.Equal(parameter.Doubles, restored.Doubles);
        Assert.Equal(BitConverter.DoubleToInt64Bits(-0.0), BitConverter.DoubleToInt64Bits(restored.Doubles[0].Value));
        Assert.Equal(parameter.Ints, restored.Ints);
        Assert.Equal(parameter.Strings, restored.Strings);
        Assert.Equal(parameter.ElementIds, restored.ElementIds);
        Assert.Equal(parameter.Fields, restored.Fields);
        Assert.Equal(parameter.DoubleFields, restored.DoubleFields);
        Assert.Equal(parameter.StringFields, restored.StringFields);
        Assert.Equal(parameter.NamedParams, restored.NamedParams);
        Assert.Equal(new uint[] { 5, 7 }, store.Select(entry => entry.Key));
        Assert.Null(store[7].CellClasses);
        Assert.Null(store[7].RoofExtrusionFaceTags);
        Assert.False(store.TryGetValue(9, out _));
    }

    [Fact]
    public void ScratchUpdateReplacesTheRecordWithoutChangingItsOrder()
    {
        using var scratch = new MemoryStream();
        var store = new NativeParameterStore(new PartitionChunks.Backing(scratch));
        store[3] = new TfBlockDeserializer.NativeParams();
        store[5] = new TfBlockDeserializer.NativeParams();

        var record = store[3];
        record.Fields["m_dbViewTypeId"] = 7;
        store[3] = record;

        Assert.Equal(new uint[] { 3, 5 }, store.Keys);
        Assert.Equal(7, store[3].Fields["m_dbViewTypeId"]);
    }
}
