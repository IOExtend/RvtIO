using System.Text;
using RvtIO.Lookup;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

/// <summary>What Format 2027 changes for the readers: schema fields flagged in <see cref="SchemaField.Extra"/> that the stream does not carry,
/// and the BuiltInCategory members Revit added after 2022.</summary>
public class Format2027SchemaTests
{
    private static SchemaField Field(string name, byte type, ushort extra = 0) => new(name, type, 0, extra, null, null, null, null);

    [Theory]
    [InlineData(0, true)]
    [InlineData(4, true)]     // VarExpr.m_refCt, present in every format since 2020 and stored
    [InlineData(2, false)]
    [InlineData(18, false)]
    [InlineData(34, false)]
    public void Stored_IsFalseOnlyWithBitTwoOfExtra(int extra, bool stored)
    {
        Assert.Equal(stored, Field("m_x", 1, (ushort)extra).Stored);
    }

    [Fact]
    public void Walk_SkipsTheFieldsTheStreamDoesNotCarry()
    {
        // GeomTable of Format 2027: m_cacheIsCurrent is declared (extra 34) between m_refPntMirrored and the counted table, but not written
        var schema = new SchemaDocument
        {
            Classes =
            [
                new(1, "Identifier", 0, 2, [Field("m_id64", 11)], []),
                new(2, "ElementId", 0, 1, [new("m_id", 14, 0, 0, null, 1, null, null)], []),
                new(3, "Table", 0, 3,
                    [Field("m_refPntMirrored", 1), Field("m_cacheIsCurrent", 1, 34),
                     new("m_table", 14, 80, 0, null, 2, null, null)], []),
            ],
        };
        var stream = new MemoryStream();
        var w = new BinaryWriter(stream, Encoding.Unicode);
        w.Write((ushort)3);
        w.Write((byte)1);      // m_refPntMirrored
        w.Write(1u); w.Write(77L);   // m_table: one ElementId (64 bits)
        var block = stream.ToArray();

        var walker = new WideBlockWalker(schema, (declaring, field) => declaring.Name + "." + field.Name, []);
        var walk = walker.Run(block, 0, block.Length, 3);

        Assert.True(walk.Exact, walk.Error);
        Assert.Equal(1, walk.Captures.Single(c => c.Key == "Table.m_table#").Integer);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HostTypeLayersFollowItsCompleteUnambiguousOwnedStructure(bool duplicate)
    {
        var pointer = new SchemaField("m_pCompoundStructure", 14, 1, 0, null, null, null, null);
        var schema = new SchemaDocument
        {
            Classes =
            [
                new(1, "Identifier", 0, 1, [Field("m_id64", 11)], []),
                new(2, "ElementId", 0, 1, [new("m_id", 14, 0, 0, null, 1, null, null)], []),
                new(3, "HostObjAttr", 0, 1, duplicate ? [pointer, pointer with { Name = "m_other" }] : [pointer], []),
                new(4, "CompoundStructure", 0, 1, [new("m_layers", 14, 0x50, 0, null, 5, null, null)], []),
                new(5, "CompoundStructureLayer", 0, 1,
                    [Field("m_layerWidth", 7), new("m_materialId", 14, 0, 0, null, 2, null, null), Field("m_layerFunction", 4)], []),
            ],
        };
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((ushort)3);
        writer.Write(uint.MaxValue);
        writer.Write((ushort)4);
        if (duplicate)
        {
            writer.Write(uint.MaxValue);
            writer.Write((ushort)4);
        }
        writer.Write(2u);
        writer.Write(0.25); writer.Write(420L); writer.Write(4);
        writer.Write(0.0); writer.Write(-1L); writer.Write(100);
        if (duplicate) writer.Write(0u);
        var block = stream.ToArray();
        var reader = new WideNativeParams(schema);
        var walk = reader.Walker.Run(block, 0, block.Length, 3);

        Assert.True(walk.Exact, walk.Error);
        Assert.Equal(TfClassNames.IdOf("CompoundStructure"),
            reader.Build(walk, 3)!.Fields[TfClassNames.Key("HostObjAttr", "m_pCompoundStructure")]);
        var layers = reader.HostTypeLayers(walk, 3);
        if (duplicate) Assert.Null(layers);
        else
        {
            Assert.NotNull(layers);
            Assert.Equal([(420, 4, 0.25, 0), (-1, 100, 0.0, 1)],
                layers.Select(layer => (layer.MaterialId, layer.Function, layer.ThicknessFt, layer.LayerIndex)));
        }
        Assert.Null(reader.HostTypeLayers(reader.Walker.Run(block, 0, block.Length - 1, 3), 3));
    }

    [Fact]
    public void CategoryNames_KnowTheMembersAddedAfter2022()
    {
        Assert.Equal("OST_BoundaryConditions", BuiltInCategoryNames.Lookup[-2005301]);
        Assert.Equal("OST_PointBoundaryConditions", BuiltInCategoryNames.Lookup[-2005302]);   // new in Revit 2027
        Assert.Equal("OST_AnalyticalMember", BuiltInCategoryNames.Lookup[-2009662]);          // new in Revit 2023
        Assert.Equal("OST_Walls", BuiltInCategoryNames.Lookup[-2000011]);
    }
}
