using System.Text.Json;
using RvtIO.Internal;
using RvtIO.Model;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

public class BasicWallTypeSerializationTests
{
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void OwnRoot_ConfirmsClassAndFunction(int function)
    {
        byte[] block = Block(function);
        Assert.True(TfBlockDeserializer.TryExtractBasicWallTypeFunction(block, 0, Size(block), 512, out int actual));
        Assert.Equal(function, actual);
    }

    [Theory]
    [InlineData("owner")] [InlineData("class")] [InlineData("footer")]
    [InlineData("header_size")] [InlineData("truncated")] [InlineData("root_truncated")]
    [InlineData("function")] [InlineData("start")] [InlineData("size")]
    public void InvalidRoot_DoesNotBecomeBasicWallSource(string mutation)
    {
        byte[] block = Block(mutation == "function" ? 6 : 1);
        uint size = Size(block);
        int start = 0;
        switch (mutation)
        {
            case "owner": block[0] ^= 1; break;
            case "class": block[12] ^= 1; break;
            case "footer": block[^1] ^= 1; break;
            case "header_size": block[8] ^= 1; break;
            case "truncated": block = block[..^1]; break;
            case "root_truncated":
                block = block[..^25]; size = (uint)block.Length - 16;
                BitConverter.GetBytes(size).CopyTo(block, 8);
                BitConverter.GetBytes(size).CopyTo(block, block.Length - 4);
                break;
            case "start": start = -1; break;
            case "size": size = uint.MaxValue; break;
        }
        Assert.False(TfBlockDeserializer.TryExtractBasicWallTypeFunction(block, start, size, 512, out _));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void DuplicateOrMalformedRoot_CannotBeReplacedByLaterSuccess(bool malformedFirst)
    {
        byte[] block = Block(1);
        var state = new ParsingState();
        var first = (byte[])block.Clone();
        if (malformedFirst) first[^1] ^= 1;
        RvtFileReader.RecordBasicWallTypeFunction(state, first, 0, Size(block), 512, 100);
        Assert.Equal(malformedFirst ? null : (int?)1, state.BasicWallTypeFunctions[100].Function);
        RvtFileReader.RecordBasicWallTypeFunction(state, block, 0, Size(block), 512, 100);
        Assert.Equal((2, (int?)null), state.BasicWallTypeFunctions[100]);
    }

    [Fact]
    public void DifferentWallTypeRoot_ForSameOwnerInvalidatesBasicSource()
    {
        byte[] block = Block(1);
        var state = new ParsingState();
        RvtFileReader.RecordBasicWallTypeFunction(state, block, 0, Size(block), 512, 100);
        RvtFileReader.RecordBasicWallTypeFunction(state, block, 0, Size(block), 2559, 100);
        Assert.Equal((2, (int?)null), state.BasicWallTypeFunctions[100]);
    }

    [Fact]
    public void Builder_UsesOnlyUniqueOwnSourceAndClearsStaleValidation()
    {
        var own = new Element { Id = 100, CategoryId = -2000011 };
        var duplicate = new Element { Id = 101, CategoryId = -2000011, SerializedBasicWallTypeFunction = 1 };
        var wrongCategory = new Element { Id = 102, CategoryId = -2000032 };
        var missing = new Element { Id = 103, CategoryId = -2000011, TypeId = 100, SerializedBasicWallTypeFunction = 1 };
        var invalid = new Element { Id = 104, CategoryId = -2000011 };
        ElementBuilder.ApplyBasicWallTypeFunctions([own, duplicate, wrongCategory, missing, invalid],
            new Dictionary<uint, (int, int?)> { [100] = (1, 2), [101] = (2, 1), [102] = (1, 1), [104] = (1, 9) });
        Assert.Equal(2, own.SerializedBasicWallTypeFunction);
        Assert.All(new[] { duplicate, wrongCategory, missing, invalid }, e => Assert.Null(e.SerializedBasicWallTypeFunction));
        Assert.DoesNotContain("SerializedBasicWallTypeFunction", JsonSerializer.Serialize(own));
    }

    [Fact]
    public void PostProcess_AttachesOwnRootValidation()
    {
        var state = new ParsingState();
        byte[] block = Block(1);
        RvtFileReader.RecordBasicWallTypeFunction(state, block, 0, Size(block), 512, 100);
        var info = new RevitFileInfo();
        info.Elements.Add(new Element { Id = 100, CategoryId = -2000011 });
        ElementBuilder.PostProcess(info, state);
        Assert.Equal(1, info.Elements.Single(e => e.Id == 100).SerializedBasicWallTypeFunction);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void BodyFunctionSentinel_IsRepairedOnlyFromItsOwnRoot(int function)
    {
        var (element, state) = BodyFallback(function);
        var parameter = Assert.Single(element.Parameters);
        var definition = parameter.Definition;
        ElementBuilder.RepairBasicWallTypeFunctionBodyFallbacks([element], state);
        Assert.Same(parameter, Assert.Single(element.Parameters));
        Assert.Same(definition, parameter.Definition);
        Assert.Equal(function, parameter.Value);
        Assert.Equal(StorageType.Integer, parameter.StorageType);
        Assert.False(parameter.IsApproximate);
    }

    [Theory]
    [InlineData("duplicate_root")] [InlineData("wrong_root_function")]
    [InlineData("missing_validation")] [InlineData("wrong_category")]
    [InlineData("missing_body")] [InlineData("duplicate_body_entry")]
    [InlineData("wrong_body_storage")] [InlineData("valid_body_value")]
    [InlineData("missing_native")] [InlineData("incomplete_native")] [InlineData("wrong_native_owner")]
    [InlineData("wrong_native_function")] [InlineData("native_int")]
    [InlineData("native_double")] [InlineData("native_string")] [InlineData("native_id")]
    [InlineData("duplicate_parameter")] [InlineData("missing_parameter")]
    [InlineData("valid_parameter_value")] [InlineData("approximate_parameter")]
    [InlineData("wrong_parameter_storage")]
    public void BodyFunctionSentinel_UnvalidatedOrStoredConflictsAreNotRewritten(string mutation)
    {
        var (element, state) = BodyFallback(1);
        var p = element.Parameters[0];
        var native = state.NativeParams[100];
        var body = state.Records[100].EditFields().BipParams;
        switch (mutation)
        {
            case "duplicate_root": state.BasicWallTypeFunctions[100] = (2, 1); break;
            case "wrong_root_function": state.BasicWallTypeFunctions[100] = (1, 0); break;
            case "missing_validation": element.SerializedBasicWallTypeFunction = null; break;
            case "wrong_category": element.CategoryId = -2000032; break;
            case "missing_body": state.Records.Clear(); break;
            case "duplicate_body_entry": body.Add(body[0]); break;
            case "wrong_body_storage": body[0] = body[0] with { FieldType = 14 }; break;
            case "valid_body_value": body[0] = body[0] with { Value = 0 }; break;
            case "missing_native": state.NativeParams.Clear(); break;
            case "incomplete_native": native.RootParameterStoresComplete = false; break;
            case "wrong_native_owner": native.Fields["m_id"] = 101; break;
            case "wrong_native_function": native.Fields["m_function"] = 0; break;
            case "native_int": native.Ints.Add((-1001006, -1)); break;
            case "native_double": native.Doubles.Add((-1001006, -1)); break;
            case "native_string": native.Strings.Add((-1001006, "-1")); break;
            case "native_id": native.ElementIds.Add((-1001006, -1)); break;
            case "duplicate_parameter": element.Parameters.Add(new Parameter { Id = -1001006, Value = -1 }); break;
            case "missing_parameter": element.Parameters.Clear(); break;
            case "valid_parameter_value": p.Value = 0; break;
            case "approximate_parameter": p.IsApproximate = true; break;
            case "wrong_parameter_storage": p.StorageType = StorageType.ElementId; break;
        }
        var values = element.Parameters.Select(p => p.Value).ToArray();
        ElementBuilder.RepairBasicWallTypeFunctionBodyFallbacks([element], state);
        Assert.Equal(values, element.Parameters.Select(p => p.Value).ToArray());
    }

    [Fact]
    public void PostProcess_ReconcilesBodyFunctionWithoutChangingTheBodySource()
    {
        var (element, state) = BodyFallback(1);
        var info = new RevitFileInfo(); info.Elements.Add(element);
        ElementBuilder.PostProcess(info, state);
        Assert.Equal(1, info.Elements.Single(e => e.Id == 100).Parameters.Single(p => p.Id == -1001006).Value);
        Assert.Equal(-1, state.Records[100].BipParams.Single().Value);
    }

    private static (Element Element, ParsingState State) BodyFallback(int function)
    {
        var element = new Element { Id = 100, CategoryId = -2000011,
            SerializedBasicWallTypeFunction = function,
            Parameters = [new Parameter { Id = -1001006, StorageType = StorageType.Integer, Value = -1,
                Definition = new Definition { Id = -1001006, Name = "FUNCTION_PARAM" } }] };
        var state = new ParsingState();
        state.BasicWallTypeFunctions[100] = (1, function);
        state.NativeParams[100] = new TfBlockDeserializer.NativeParams { RootParameterStoresComplete = true };
        state.NativeParams[100].Fields["m_id"] = 100;
        state.NativeParams[100].Fields["m_function"] = function;
        state.Records[100] = new ElementRecord { ElemId = 100,
            BipParams = [new BipEntry(-1001006, "FUNCTION_PARAM", 4, -1)] };
        return (element, state);
    }

    [Theory]
    [InlineData(1u)] [InlineData(100001u)]
    public void IncompleteParameterStore_DoesNotConfirmAbsenceOfStoredFunction(uint count)
    {
        byte[] block = Block(1);
        var complete = TfBlockDeserializer.ExtractParams(block, 0, Size(block), 512);
        Assert.True(complete!.RootParameterStoresComplete);
        BitConverter.GetBytes(count).CopyTo(block, block.Length - 8);
        var partial = TfBlockDeserializer.ExtractParams(block, 0, Size(block), 512);
        Assert.NotNull(partial);
        Assert.False(partial.RootParameterStoresComplete);
        Assert.True(TfBlockDeserializer.TryExtractBasicWallTypeFunction(block, 0, Size(block), 512, out _));
        var (element, state) = BodyFallback(1);
        state.NativeParams[100] = partial;
        ElementBuilder.RepairBasicWallTypeFunctionBodyFallbacks([element], state);
        Assert.Equal(-1, Assert.Single(element.Parameters).Value);
    }

    private static uint Size(byte[] block) => BitConverter.ToUInt32(block, 8);

    private static byte[] Block(int function)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(100u); writer.Write(0u); writer.Write(0u); writer.Write((ushort)512);
        foreach (var field in TfBlockDeserializer.SerializationFields(512)!)
        {
            if (field.Name == "m_pParamValueSetDouble")
            {
                writer.Write(uint.MaxValue); writer.Write((ushort)2887); continue;
            }
            if (field.Declaring == 37 && field.Name == "m_id") { writer.Write(100); continue; }
            if (field.Declaring == 513 && field.Name == "m_function") { writer.Write(function); continue; }
            if (field.Name == "m_bordMullions") { writer.Write(0L); writer.Write(0L); continue; }
            if ((field.Mods >> 4) == 5) { writer.Write(0u); continue; }
            int count = (field.Mods >> 4) == 1 ? field.Cnt!.Value : 1;
            for (int i = 0; i < count; i++)
                switch (field.Type)
                {
                    case 1: writer.Write((byte)0); break;
                    case 4: writer.Write(0); break;
                    case 14: writer.Write(field.Ref == 20 ? -1 : 0); break;
                    default: throw new InvalidOperationException(field.Name);
                }
        }
        writer.Write(0u); // Empty deferred double-parameter store.
        writer.Write(0u); // Footer.
        byte[] block = stream.ToArray();
        uint size = (uint)block.Length - 16;
        BitConverter.GetBytes(size).CopyTo(block, 8);
        BitConverter.GetBytes(size).CopyTo(block, block.Length - 4);
        return block;
    }
}
