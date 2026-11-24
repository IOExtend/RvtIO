using RvtIO.Internal;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

public class ElementRecordFieldStoreTests
{
    [Fact]
    public void ArchivedFieldsCanBeReadBeforeAndAfterPageSeal()
    {
        using var scratch = new MemoryStream();
        var store = new ElementRecordFieldStore(new PartitionChunks.Backing(scratch));
        var record = new ElementRecord { ElemId = 1, Body = [1, 2, 3] };
        var fields = record.EditFields();
        fields.References.AddRange([23, 11, 23]);
        fields.BipParams.AddRange([new(-1001203, "ALL_MODEL_MARK", 3, -1), new(50, null, 205, null)]);
        fields.WritableParamIds.Add(50);
        fields.EntryValues.AddRange([-1, 11, 0]);
        record.ArchiveFields(store);

        Assert.Equal(fields.References, record.References);
        Assert.Throws<InvalidOperationException>(() => record.EditFields());

        var other = new ElementRecord { ElemId = 2 };
        other.EditFields().References.AddRange(Enumerable.Repeat(123u, 20_000));
        other.ArchiveFields(store);
        store.Seal();
        Assert.True(scratch.Length > 64 * 1024);
        Assert.Equal(20_000, other.References.Count);

        Assert.Equal(fields.References, record.References);
        Assert.Equal(fields.BipParams, record.BipParams);
        Assert.True(record.IsWritable(50));
        Assert.False(record.IsWritable(-1001203));
        Assert.Equal(fields.EntryValues, record.EntryValues);
        Assert.Equal(new byte[] { 1, 2, 3 }, record.Body);
    }
}
