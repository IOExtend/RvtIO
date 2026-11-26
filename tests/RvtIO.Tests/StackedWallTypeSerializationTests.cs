using System.Text.Json;
using RvtIO.Ifc;
using RvtIO.Internal;
using RvtIO.Model;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

public class StackedWallTypeSerializationTests
{
    [Fact]
    public void CompleteOwnRoot_ConfirmsStackedTypeWithoutCategoryOrNameGuessing()
    {
        byte[] block = Block();
        Assert.True(TfBlockDeserializer.IsExactStackedWallTypeRoot(block, 0, Size(block), 3666));
        var state = State(block);
        var info = new RevitFileInfo();
        var type = new Element { Id = 100, CategoryId = -2003500 };
        info.Elements.Add(type);
        ElementBuilder.PostProcess(info, state);
        Assert.True(type.IsSerializedStackedWallType);
        Assert.DoesNotContain("IsSerializedStackedWallType", JsonSerializer.Serialize(type));
    }

    [Theory]
    [InlineData("footer")] [InlineData("owner")] [InlineData("zero_owner")]
    [InlineData("class")] [InlineData("size")] [InlineData("truncated_root")]
    [InlineData("negative_start")] [InlineData("oversized")]
    public void MalformedOrUnownedRoot_FailsClosed(string mutation)
    {
        byte[] block = Block();
        int start = 0; uint size = Size(block);
        switch (mutation)
        {
            case "footer": block[^1] ^= 1; break;
            case "owner": BitConverter.GetBytes(101u).CopyTo(block, 0); break;
            case "zero_owner": BitConverter.GetBytes(0u).CopyTo(block, 0); break;
            case "class": BitConverter.GetBytes((ushort)2559).CopyTo(block, 12); break;
            case "size": size--; break;
            case "negative_start": start = -1; break;
            case "oversized": size = uint.MaxValue; break;
            case "truncated_root":
                block = block[..^5].Concat(new byte[4]).ToArray();
                size = (uint)block.Length - 16;
                BitConverter.GetBytes(size).CopyTo(block, 8);
                BitConverter.GetBytes(size).CopyTo(block, block.Length - 4);
                break;
        }
        Assert.False(TfBlockDeserializer.IsExactStackedWallTypeRoot(block, start, size, 3666));
    }

    [Theory]
    [InlineData("duplicate")] [InlineData("different_wall_root")] [InlineData("category")]
    [InlineData("wrong_id")] [InlineData("no_root")] [InlineData("no_count")]
    public void MissingOrAmbiguousSource_ClearsPreviouslyAttachedFlag(string mutation)
    {
        byte[] block = Block(); var state = State(block);
        var type = new Element { Id = 100, CategoryId = -2003500, IsSerializedStackedWallType = true };
        switch (mutation)
        {
            case "duplicate": RvtFileReader.RecordBasicWallTypeFunction(state, block, 0, Size(block), 3666, 100); break;
            case "different_wall_root": RvtFileReader.RecordBasicWallTypeFunction(state, block, 0, Size(block), 512, 100); break;
            case "category": type.CategoryId = -2000011; break;
            case "wrong_id": type.Id = 101; break;
            case "no_root": state.StackedWallTypeOwners.Clear(); break;
            case "no_count": state.BasicWallTypeFunctions.Clear(); break;
        }
        ElementBuilder.ApplyStackedWallTypes([type], state);
        Assert.False(type.IsSerializedStackedWallType);
    }

    [Theory]
    [InlineData("valid", true)] [InlineData("generic_type", false)]
    [InlineData("category", false)] [InlineData("type_category", false)]
    [InlineData("no_validation", false)] [InlineData("missing_type", false)]
    [InlineData("mismatched_id", false)]
    [InlineData("stacked_category", true)] [InlineData("stacked_without_type", false)]
    [InlineData("stacked_type_itself", false)]
    public void ExportSelection_RequiresCanonicalOwnType(string mutation, bool expected)
    {
        var wall = new Element { Id = 1, CategoryId = -2000011, TypeId = 100,
            Parameters = [new Parameter { Id = -1002050, Definition = new Definition { Id = -1002050, Name = "ELEM_TYPE_PARAM" },
                StorageType = StorageType.ElementId, Value = 100 }] };
        var type = new Element { Id = 100, CategoryId = -2003500, IsSerializedStackedWallType = true };
        var types = new Dictionary<int, Element> { [100] = type };
        switch (mutation)
        {
            case "generic_type": wall.Parameters.Clear(); break;
            case "category": wall.CategoryId = -2000170; break;
            case "type_category": type.CategoryId = -2000011; break;
            case "no_validation": type.IsSerializedStackedWallType = false; break;
            case "missing_type": types.Clear(); break;
            case "mismatched_id": type.Id = 101; break;
            case "stacked_category": wall.CategoryId = -2003500; break;
            case "stacked_without_type": wall.CategoryId = -2003500; type.IsSerializedStackedWallType = false; break;
            case "stacked_type_itself": wall.CategoryId = -2003500; wall.Id = type.Id; break;
        }
        Assert.Equal(expected, IfcExporter.IsValidatedStackedWallContainer(wall, types));
    }

    private static ParsingState State(byte[] block)
    {
        var state = new ParsingState();
        RvtFileReader.RecordStackedWallTypeRoot(state, block, 0, Size(block), 3666, 100);
        RvtFileReader.RecordBasicWallTypeFunction(state, block, 0, Size(block), 3666, 100);
        return state;
    }

    private static uint Size(byte[] block) => BitConverter.ToUInt32(block, 8);

    private static byte[] Block()
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write(100u); writer.Write(0u); writer.Write(0u); writer.Write((ushort)3666);
        foreach (var field in TfBlockDeserializer.SerializationFields(3666)!)
        {
            if (field.Declaring == 37 && field.Name == "m_id") { writer.Write(100); continue; }
            if (field.Name == "m_bordMullions") { writer.Write(0L); writer.Write(0L); continue; }
            if ((field.Mods >> 4) == 5) { writer.Write(0u); continue; }
            int count = (field.Mods >> 4) == 1 ? field.Cnt!.Value : 1;
            for (int i = 0; i < count; i++)
                switch (field.Type)
                {
                    case 1: writer.Write((byte)0); break;
                    case 4: writer.Write(0); break;
                    case 7: writer.Write(0d); break;
                    case 14: writer.Write(field.Ref == 20 ? -1 : 0); break;
                    default: throw new InvalidOperationException(field.Name);
                }
        }
        writer.Write(0u);
        byte[] block = stream.ToArray(); uint size = (uint)block.Length - 16;
        BitConverter.GetBytes(size).CopyTo(block, 8);
        BitConverter.GetBytes(size).CopyTo(block, block.Length - 4);
        return block;
    }
}
