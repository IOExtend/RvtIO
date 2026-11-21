using System.Reflection;
using System.Text.Json;
using RvtIO.Ifc;
using RvtIO.Internal;
using RvtIO.Model;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

public class SystemTypeIdentityTests
{
    [Theory]
    [InlineData(3524)] [InlineData(361)] [InlineData(1913)] [InlineData(642)]
    public void CompleteOwnOccurrenceRoot_ProvidesItsTypeLink(int classId)
    {
        byte[] block = Block((ushort)classId);
        Assert.Equal(500, TfBlockDeserializer.ExtractSystemOccurrenceTypeId(block, 0, Size(block), (ushort)classId));
    }

    [Theory]
    [InlineData("footer")] [InlineData("size")] [InlineData("class")]
    [InlineData("owner")] [InlineData("zero_owner")] [InlineData("zero_type")]
    [InlineData("negative_type")] [InlineData("self_type")] [InlineData("truncated")]
    public void InvalidRoot_DoesNotProduceATypeLink(string mutation)
    {
        foreach (ushort cls in new ushort[] { 3524, 361, 1913, 642 })
        {
            byte[] block = Block(cls, mutation);
            if (mutation == "footer") block[^1] ^= 1;
            if (mutation == "size") block[8] ^= 1;
            if (mutation == "class") block[12] ^= 1;
            if (mutation == "owner") block[0] ^= 1;
            if (mutation == "zero_owner") BitConverter.GetBytes(0).CopyTo(block, 0);
            Assert.Null(TfBlockDeserializer.ExtractSystemOccurrenceTypeId(block, 0, Size(block), cls));
        }
    }

    [Fact]
    public void WideFile_RecordsAnOccurrenceOnce_WhenItsTranscodedCopyIsSeenAgain()
    {
        var state = new ParsingState { WideSchema = new FileSchema(new SchemaDocument()) };
        state.SystemOccurrenceTypeIds[100] = (1, 3524, 500);
        state.SchemaSystemOccurrenceOwners.Add(100);
        byte[] block = Block(3524);
        RvtFileReader.RecordSystemOccurrenceTypeId(state, block, 0, Size(block), 3524, 100);
        Assert.Equal((1, (ushort)3524, (int?)500), state.SystemOccurrenceTypeIds[100]);
    }

    [Theory]
    [InlineData(2, 512, false, true)]
    [InlineData(3, 512, false, false)]
    [InlineData(2, 2559, false, false)]
    [InlineData(2, 512, true, false)]
    [InlineData(null, 512, false, true)]
    [InlineData(null, 512, true, false)]
    public void UniqueCurrentBasicWall_RecoversItsOwnTypedRootBeyondTheSequentialScan(
        int? route, ushort typeClass, bool duplicate, bool recovered)
    {
        byte[] block = Block(3524);
        var frame = new PartitionFraming.Framed([block], [new(102, 100, 0, block.Length)], 4, 2);
        var state = new ParsingState
        {
            AllKnownIds = [100, 500],
            ElemRecOwningElementIds = new() { [100] = -1 },
            FramedPartitions = duplicate ? [frame, frame with { Partition = 3 }] : [frame],
            IncrementRoutedPartitions = route is int partition ? new Dictionary<ulong, int> { [100] = partition } : null,
            StaleCopiesRemoved = true,
        };
        state.SystemTypeRoots[500] = (1, typeClass, null);

        RvtFileReader.RunTfStreamWalk(state);

        Assert.Equal(recovered, state.NativeParamsFromRootClass.Contains(100));
        if (recovered)
        {
            Assert.Equal(500, NativeTypePointers.Of(3524, state.NativeParams[100]));
            Assert.Equal((1, (ushort)3524, (int?)500), state.SystemOccurrenceTypeIds[100]);
        }
        else Assert.False(state.SystemOccurrenceTypeIds.ContainsKey(100));
    }

    [Fact]
    public void BoundsAreValidated_AndOtherHostRootsCannotBorrowTheField()
    {
        byte[] block = Block(3524);
        Assert.Null(TfBlockDeserializer.ExtractSystemOccurrenceTypeId(block, -1, Size(block), 3524));
        Assert.Null(TfBlockDeserializer.ExtractSystemOccurrenceTypeId(block, int.MaxValue, Size(block), 3524));
        Assert.Null(TfBlockDeserializer.ExtractSystemOccurrenceTypeId(block, 0, uint.MaxValue, 3524));
        Assert.Null(TfBlockDeserializer.ExtractSystemOccurrenceTypeId(block, 0, Size(block), 362));
        Assert.Null(TfBlockDeserializer.ExtractSystemOccurrenceTypeId(block, 0, Size(block), 1794));
    }

    [Theory]
    [InlineData("duplicate", false)] [InlineData("malformed_first", false)]
    [InlineData("unsupported_root", false)] [InlineData("walker_owner", false)]
    [InlineData("duplicate", true)] [InlineData("malformed_first", true)]
    [InlineData("unsupported_root", true)] [InlineData("walker_owner", true)]
    public void RootSource_IsNotRepairedByASecondRoot(string mode, bool wide)
    {
        var state = new ParsingState { WideSchema = wide ? new FileSchema(new SchemaDocument()) : null };
        byte[] block = Block(3524);
        if (mode == "malformed_first")
            RvtFileReader.RecordSystemOccurrenceTypeId(state, Block(3524, "truncated"), 0, Size(Block(3524, "truncated")), 3524, 100);
        RvtFileReader.RecordSystemOccurrenceTypeId(state, block, 0, Size(block), 3524, mode == "walker_owner" ? 999u : 100u);
        if (mode == "duplicate") RvtFileReader.RecordSystemOccurrenceTypeId(state, block, 0, Size(block), 3524, 100);
        if (mode == "unsupported_root") RvtFileReader.RecordSystemOccurrenceTypeId(state, block, 0, Size(block), 4157, 100);
        var validation = state.SystemOccurrenceTypeIds[mode == "walker_owner" ? 999u : 100u];
        Assert.Null(validation.TypeId);
        Assert.Equal(mode == "walker_owner" ? 1 : 2, validation.BlockCount);
    }

    [Theory]
    [InlineData(3524, 512, -2000011, null)]
    [InlineData(361, 2559, -2000011, null)]
    [InlineData(1913, 1915, -2000032, 0)]
    [InlineData(1913, 1915, -2001300, 1)]
    [InlineData(642, 646, -2000038, null)]
    public void UniqueCompatibleRoots_ProvideIdentityWithoutNamesOrLocalization(int occurrenceClass, int typeClass, int category, int? discriminator)
    {
        var (elements, state) = Source((ushort)occurrenceClass, (ushort)typeClass, category, discriminator);
        string before = JsonSerializer.Serialize(elements);
        ElementBuilder.ApplySerializedSystemTypeIdentity(elements, state);
        Assert.Equal(500, elements[0].SerializedSystemTypeId);
        Assert.Equal((ushort)typeClass, elements[1].SerializedSystemTypeClassId);
        Assert.Equal(before, JsonSerializer.Serialize(elements)); // No public type/name/parameter mutation.
        ElementBuilder.ApplySerializedSystemTypeIdentity(elements, new ParsingState());
        Assert.Null(elements[0].SerializedSystemTypeId);
        Assert.Null(elements[1].SerializedSystemTypeClassId);
    }

    [Theory]
    [InlineData("missing_occurrence")] [InlineData("missing_type_root")]
    [InlineData("duplicate_occurrence_root")] [InlineData("duplicate_type_root")]
    [InlineData("duplicate_occurrence")] [InlineData("duplicate_type")]
    [InlineData("wrong_occurrence_class")] [InlineData("wrong_type_class")]
    [InlineData("wrong_occurrence_category")] [InlineData("wrong_type_category")]
    [InlineData("self_type")] [InlineData("unresolved_type")]
    [InlineData("block_only_occurrence")] [InlineData("block_only_type")]
    public void UnvalidatedOwnershipOrClass_KeepsIdentityUnset(string mutation)
    {
        var (elements, state) = Source(3524, 512, -2000011, null);
        switch (mutation)
        {
            case "missing_occurrence": state.SystemOccurrenceTypeIds.Clear(); break;
            case "missing_type_root": state.SystemTypeRoots.Clear(); break;
            case "duplicate_occurrence_root": state.SystemOccurrenceTypeIds[100] = (2, 3524, 500); break;
            case "duplicate_type_root": state.SystemTypeRoots[500] = (2, 512, null); break;
            case "duplicate_occurrence": elements.Add(new Element { Id = 100 }); break;
            case "duplicate_type": elements.Add(new Element { Id = 500 }); break;
            case "wrong_occurrence_class": state.SystemOccurrenceTypeIds[100] = (1, 4157, 500); break;
            case "wrong_type_class": state.SystemTypeRoots[500] = (1, 3666, null); break;
            case "wrong_occurrence_category": elements[0].CategoryId = -2000032; break;
            case "wrong_type_category": elements[1].CategoryId = -2000032; break;
            case "self_type": state.SystemOccurrenceTypeIds[100] = (1, 3524, 100); break;
            case "unresolved_type": elements.RemoveAt(1); break;
            case "block_only_occurrence": elements[0].IsBlockOnly = true; break;
            case "block_only_type": elements[1].IsBlockOnly = true; break;
        }
        ElementBuilder.ApplySerializedSystemTypeIdentity(elements, state);
        Assert.All(elements, e => Assert.Null(e.SerializedSystemTypeId));
        Assert.All(elements, e => Assert.Null(e.SerializedSystemTypeClassId));
    }

    [Theory]
    [InlineData(-2000032, null)] [InlineData(-2000032, 1)]
    [InlineData(-2001300, 0)] [InlineData(-2000032, 2)]
    public void FloorDiscriminatorMustAgreeWithItsCategory(int category, int? discriminator)
    {
        var (elements, state) = Source(1913, 1915, category, discriminator);
        ElementBuilder.ApplySerializedSystemTypeIdentity(elements, state);
        Assert.Null(elements[0].SerializedSystemTypeId);
    }

    [Theory]
    [InlineData("IFCWALL", 512)] [InlineData("IFCWALLSTANDARDCASE", 512)]
    [InlineData("IFCCURTAINWALL", 512)] [InlineData("IFCCURTAINWALL", 2559)]
    [InlineData("IFCSLAB", 1915)] [InlineData("IFCCOVERING", 646)]
    public void SystemGuid_ComesFromTheValidatedOwnType_NotTheOccurrence(string product, int typeClass)
    {
        var (writer, sources) = TypeWriter(product, (ushort)typeClass);
        string nativeGuid = IfcGuid.ToIfcGuid(Guid.Parse("f9e8f60b-5fb9-4e82-999e-86cc1d8b37eb"));
        Assert.Equal(nativeGuid, Assert.Single(sources).Value);
        Assert.Equal((1, 1), Apply(writer, sources));
        Assert.StartsWith($"'{nativeGuid}',", writer.EntityAt(writer.EntityCount - 2).Args);
        Assert.StartsWith($"'{IfcGuid.FromMd5("IfcRelDefinesByType:" + nativeGuid)}',", writer.EntityAt(writer.EntityCount - 1).Args);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void EveryGroupMemberMustConfirmTheSameIdentity_InEitherOrder(bool reverse)
    {
        var (writer, sources) = TypeWriter("IFCWALL", 512, elements =>
        {
            elements[101] = new Element { Id = 101, TypeId = 500, CategoryId = -2000011,
                Parameters = [TypeParameter(500)] };
        }, reverse);
        Assert.Empty(sources);
        Assert.Equal((0, 0), Apply(writer, sources));
    }

    [Theory]
    [InlineData("missing_link")] [InlineData("wrong_link")]
    [InlineData("missing_root")] [InlineData("wrong_root")]
    [InlineData("public_conflict")] [InlineData("missing_export_id")]
    [InlineData("override")] [InlineData("wrong_category")]
    [InlineData("mismatched_category")]
    public void ExporterCannotUpgradeAnUnvalidatedSystemGuid(string mutation)
    {
        var (_, sources) = TypeWriter("IFCWALL", 512, elements =>
        {
            switch (mutation)
            {
                case "missing_link": elements[100].SerializedSystemTypeId = null; break;
                case "wrong_link": elements[100].SerializedSystemTypeId = 501; break;
                case "missing_root": elements[500].SerializedSystemTypeClassId = null; break;
                case "wrong_root": elements[500].SerializedSystemTypeClassId = 1915; break;
                case "public_conflict": elements[100].TypeId = 999; break;
                case "missing_export_id": elements[500].SerializedExportId = null; break;
                case "override": elements[500].Parameters.Add(new Parameter { Definition = new Definition { Id = -1019001 }, StorageType = StorageType.String, Value = "unresolved" }); break;
                case "wrong_category": elements[100].CategoryId = elements[500].CategoryId = -2000038; break;
                case "mismatched_category": elements[100].CategoryId = -2000038; break;
            }
        });
        Assert.Empty(sources);
    }

    private static (List<Element>, ParsingState) Source(ushort occurrenceClass, ushort typeClass, int category, int? discriminator)
    {
        var state = new ParsingState();
        state.SystemOccurrenceTypeIds[100] = (1, occurrenceClass, 500);
        state.SystemTypeRoots[500] = (1, typeClass, discriminator);
        return ([new Element { Id = 100, CategoryId = category }, new Element { Id = 500, CategoryId = category }], state);
    }

    private static Parameter TypeParameter(int id) => new()
    {
        Id = -1002050, StorageType = StorageType.ElementId,
        Definition = new Definition { Id = -1002050, Name = "ELEM_TYPE_PARAM" }, Value = id,
    };

    private static (StepWriter, Dictionary<int, string>) TypeWriter(string product, ushort typeClass,
        Action<Dictionary<int, Element>>? configure = null, bool reverse = false)
    {
        int category = typeClass switch { 1915 => -2000032, 646 => -2000038, _ => -2000011 };
        var elements = new Dictionary<int, Element>
        {
            [100] = new() { Id = 100, TypeId = 500, CategoryId = category, SerializedSystemTypeId = 500,
                Parameters = [TypeParameter(500)], SerializedExportId = Guid.Parse("92e43d92-97b4-4571-b05c-fb9c470f9e1c") },
            [500] = new() { Id = 500, CategoryId = category, SerializedSystemTypeClassId = typeClass,
                SerializedExportId = Guid.Parse("f9e8f60b-5fb9-4e82-999e-86cc1d8b37eb") },
        };
        configure?.Invoke(elements);
        var products = elements.Values.Where(e => e.Id != 500).Select(e => e.Id).ToList();
        if (reverse) products.Reverse();
        var writer = new StepWriter();
        var method = typeof(IfcExporter).GetMethod("WriteTypeObjectsAndRelDefinesByType", BindingFlags.NonPublic | BindingFlags.Static)!;
        var sources = Assert.IsType<Dictionary<int, string>>(method.Invoke(null,
            [writer, 900, elements, products.ToDictionary(id => id, id => (1000 + id, 0, product)),
                new Dictionary<(string, string), int>(), new Dictionary<int, bool?>()]));
        return (writer, sources);
    }

    private static (int, int) Apply(StepWriter writer, Dictionary<int, string> sources) =>
        Assert.IsType<(int, int)>(typeof(IfcExporter).GetMethod("ApplyTypeGlobalIds", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [writer, sources]));

    private static uint Size(byte[] block) => BitConverter.ToUInt32(block, 8);

    private static byte[] Block(ushort cls, string? mutation = null)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write(100); writer.Write(0u); writer.Write(0u); writer.Write(cls);
        foreach (var field in TfBlockDeserializer.SerializationFields(cls)!)
        {
            if (field.Declaring == 37 && field.Name == "m_docAccess") { writer.Write(1u); continue; }
            if (field.Declaring == 37 && field.Name == "m_id") { writer.Write(100); continue; }
            if (field.Name is "m_WallAttributesId" or "m_floorAttributesId" or "m_ceilingAttributesId")
            {
                if (mutation == "truncated") break;
                writer.Write(mutation switch { "zero_type" => 0, "negative_type" => -1, "self_type" => 100, _ => 500 });
                continue;
            }
            WriteDefault(field);
        }
        writer.Write(0u);
        byte[] block = stream.ToArray(); uint size = (uint)block.Length - 16;
        BitConverter.GetBytes(size).CopyTo(block, 8); BitConverter.GetBytes(size).CopyTo(block, block.Length - 4);
        return block;

        void WriteDefault(TfBlockDeserializer.TfField field)
        {
            if (field.Mods >> 4 == 5) { writer.Write(0u); return; }
            int count = field.Mods >> 4 == 1 ? field.Cnt!.Value : 1;
            for (int i = 0; i < count; i++) switch (field.Type)
            {
                case 1: case 2: writer.Write((byte)0); break;
                case 4: writer.Write(0); break;
                case 7: writer.Write(0d); break;
                case 8: writer.Write(0u); break;
                case 13 when field.Declaring == 513 && field.Name == "m_bordMullions": writer.Write(0L); break;
                case 14 when (field.Mods & 15) == 0 && field.Ref is not null and not 20:
                    foreach (var nested in TfBlockDeserializer.SerializationFields(field.Ref.Value)!) WriteDefault(nested); break;
                case 14: writer.Write(field.Ref == 20 ? -1 : 0); break;
                default: throw new InvalidOperationException(field.Name);
            }
        }
    }
}
