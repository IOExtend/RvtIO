using RvtIO.Parsing;
using RvtIO.Writing;
using Xunit;

namespace RvtIO.Tests;

public sealed class DocumentIncrementFixupTests
{
    private static readonly byte[] Schema = RecordRoutingTests.Schema();

    [Fact]
    public void RefreshTotals_UpdatesOnlyTheCurrentLocalCountAndItsOwnVersion()
    {
        byte[] data = RecordRoutingTests.Table([(3, 7, [(-1, 7)]), (4, 5, [(-1, 5), (2, 4)])]);
        var patched = DocumentIncrementFixup.RefreshTotals(data, TfSchemaReader.Parse(Schema), 6);
        var schema = FormatsSchema.TryParse(Schema)!;
        var before = DocumentIncrementTable.TryParse(data, schema, out _)!;
        var after = DocumentIncrementTable.TryParse(patched, schema, out _)!;

        Assert.True(before.Increments.Zip(after.Increments).All(pair => pair.First.SameRouting(pair.Second)));
        Assert.True(before.Local[0].SameRouting(after.Local[0]));
        Assert.Equal(5, before.Local[^1].TotalElements);
        Assert.Equal(6, after.Local[^1].TotalElements);
        Assert.Equal([new(-1, 6), new(2, 4)], after.Local[^1].Versions);
    }

    [Fact]
    public void RefreshTotals_LeavesConsistentTableUntouched()
    {
        byte[] data = RecordRoutingTests.Table([(3, 7, [(-1, 7)])]);
        Assert.Same(data, DocumentIncrementFixup.RefreshTotals(data, TfSchemaReader.Parse(Schema), 7));
    }
}
