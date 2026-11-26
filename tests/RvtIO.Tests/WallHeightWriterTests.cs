using RvtIO.Parsing;
using RvtIO.Writing;
using Xunit;

namespace RvtIO.Tests;

public class WallHeightWriterTests
{
    [Theory]
    [InlineData("PlanTopologies", 0, false, true)]
    [InlineData("PlanTopologies", 1, false, false)]
    [InlineData("PlanTopologies", 0, true, false)]
    [InlineData("PlanTopologies", null, false, false)]
    [InlineData("UnverifiedPlanTopologies", 0, false, false)]
    [InlineData(null, null, false, false)]
    public void RequiresACompleteEmptyLevelSet(string? setClass, int? levelCount, bool trailingByte, bool empty)
    {
        var schema = new SchemaDocument
        {
            Classes =
            [
                new(1, "AreaPlanTopologiesInterface", 0, 1, [new("m_pSet", 14, 1, 0, null, null, null, null)], []),
                new(2, setClass ?? "PlanTopologies", 0, 1,
                    levelCount.HasValue ? [new("m_set", 4, 80, 0, null, null, null, null)] : [], []),
            ],
        };
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((ushort)1);
        writer.Write(setClass is null ? 0u : uint.MaxValue);
        if (setClass is not null)
            writer.Write((ushort)2);
        if (levelCount is int count)
        {
            writer.Write(count);
            for (int i = 0; i < count; i++)
                writer.Write(i + 10);
        }
        if (trailingByte)
            writer.Write((byte)0);
        byte[] body = stream.ToArray();
        var walker = new WideBlockWalker(schema, (declaring, field) =>
            WideBlockWalker.AllItemsKeyPrefix + declaring.Name + "." + field.Name, []);

        Assert.Equal(empty, WallHeightWriter.HasEmptyPlanTopologies(walker.Run(body, 0, body.Length, 1), walker));
    }
}
