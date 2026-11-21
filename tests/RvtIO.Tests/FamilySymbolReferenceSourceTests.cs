using System.Text;
using RvtIO.Internal;
using RvtIO.Model;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

public class FamilySymbolReferenceSourceTests
{
    [Theory]
    [InlineData(-2000150, true, false, true)]
    [InlineData(-2000150, false, false, false)]
    [InlineData(-2000150, true, true, false)]
    [InlineData(-2002000, true, false, false)]
    public void CurrentFramedAnnotationUsesItsOwnUnambiguousSymbol(
        int category, bool hasSymbol, bool duplicate, bool recovered)
    {
        var header = new byte[22];
        BitConverter.GetBytes(100u).CopyTo(header, 0);
        BitConverter.GetBytes(10u).CopyTo(header, 4);
        BitConverter.GetBytes((ushort)0x053C).CopyTo(header, 8);
        BitConverter.GetBytes(category).CopyTo(header, 14);
        BitConverter.GetBytes(10u).CopyTo(header, 18);
        byte[] root = Block(1794, project: true), symbol = Block(1829, project: true);
        var instance = new PartitionFraming.Framed([[.. header, .. root]],
            [new(101, 100, 0, header.Length), new(102, 100, header.Length, root.Length)], 4, 2);
        var frames = new List<PartitionFraming.Framed?> { instance };
        if (hasSymbol) frames.Add(new([symbol], [new(102, 500, 0, symbol.Length)], 4, 1));
        if (duplicate) frames.Add(instance with { Partition = 3 });
        var state = new ParsingState
        {
            AllKnownIds = [100, 500],
            ElemRecOwningElementIds = new() { [100] = -1, [500] = -1 },
            FramedPartitions = frames,
            StaleCopiesRemoved = true,
        };
        var annotation = new Element { Id = 100, TypeId = 123, CategoryId = category };

        RvtFileReader.RunTfStreamWalk(state);
        ElementBuilder.ApplyNativeParams(new RevitFileInfo { Elements = [annotation] }, state);

        Assert.Equal(recovered, state.NativeParamsFromRootClass.Contains(100));
        Assert.Equal(recovered ? 500 : 123, annotation.TypeId);
    }

    [Fact]
    public void DoorNameAfterCanonicalizationUsesTheOwnSymbolNameRatherThanAnEarlierTypeGuess()
    {
        var state = State();
        byte[] instanceBlock = Block(1794);
        state.NativeParams[100] = TfBlockDeserializer.ExtractParams(instanceBlock, 0, Size(instanceBlock), 1794)!;
        var instance = new Element { Id = 100, CategoryId = -2000023, TypeId = 999, Name = "Earlier type guess", References = [500] };
        var type = new Element { Id = 500, CategoryId = -2000023, Name = "Inherited type mark" };
        var info = new RevitFileInfo(); info.Elements.AddRange([instance, type]);
        ElementBuilder.PostProcess(info, state);
        Assert.Equal(500, instance.TypeId);
        Assert.Equal("Own family symbol", instance.Name);
        Assert.Equal("Own family symbol", type.Name);
        var document = RvtDocument.FromInfo(info);
        Assert.Equal("Own family symbol", document.GetElement(instance.Id)!.Name);
    }

    [Theory]
    [InlineData("missing_link")] [InlineData("duplicate_link")] [InlineData("null_link")]
    [InlineData("negative_link")] [InlineData("self_link")]
    [InlineData("missing_name")] [InlineData("duplicate_name")] [InlineData("null_name")]
    [InlineData("blank_name")] [InlineData("missing_target")] [InlineData("wrong_target_category")]
    [InlineData("duplicate_instance")] [InlineData("duplicate_target")] [InlineData("wrong_instance_category")]
    public void DoorNamesRejectMissingAmbiguousOrUnownedSources(string mode)
    {
        var state = State();
        var instance = new Element { Id = 100, CategoryId = -2000023, TypeId = 500, Name = "Keep existing" };
        var type = new Element { Id = 500, CategoryId = -2000023, Name = "Not the own name" };
        var elements = new List<Element> { instance, type };
        switch (mode)
        {
            case "missing_link": state.FamilyInstanceSymbolIds.Clear(); break;
            case "duplicate_link": state.FamilyInstanceSymbolIds[100] = (2, 500); break;
            case "null_link": state.FamilyInstanceSymbolIds[100] = (1, null); break;
            case "negative_link": state.FamilyInstanceSymbolIds[100] = (1, -1); break;
            case "self_link": state.FamilyInstanceSymbolIds[100] = (1, 100); break;
            case "missing_name": state.FamilySymbolNames.Clear(); break;
            case "duplicate_name": state.FamilySymbolNames[500] = (2, "Own family symbol"); break;
            case "null_name": state.FamilySymbolNames[500] = (1, null); break;
            case "blank_name": state.FamilySymbolNames[500] = (1, " "); break;
            case "missing_target": elements.Remove(type); break;
            case "wrong_target_category": type.CategoryId = -2000014; break;
            case "wrong_instance_category": instance.CategoryId = -2000014; break;
            case "duplicate_instance": elements.Add(new Element { Id = 100 }); break;
            case "duplicate_target": elements.Add(new Element { Id = 500 }); break;
        }
        ElementBuilder.ApplyValidatedDoorTypeNames(elements, state);
        Assert.Equal("Keep existing", instance.Name);
        Assert.Equal("Not the own name", type.Name);
    }

    [Fact]
    public void DoorOwnNameDoesNotMutateThePublicTypeLinkOrExplicitReference()
    {
        var instance = new Element { Id = 100, CategoryId = -2000023, TypeId = 999, Name = "Earlier guess" };
        var type = new Element { Id = 500, CategoryId = -2000023, Name = "Keep target name" };
        instance.Parameters.Add(new Parameter { Id = 700, Definition = new() { Name = "Reference" },
            StorageType = StorageType.String, Value = "Explicit reference" });
        ElementBuilder.ApplyValidatedDoorTypeNames([instance, type], State());
        Assert.Equal("Own family symbol", instance.Name);
        Assert.Equal(999, instance.TypeId);
        Assert.Equal("Explicit reference", ExactIfcStringParameterResolver.ResolveReference(instance, type).Value);
        Assert.Equal("Own family symbol", type.Name);
    }

    [Fact]
    public void OwnInstanceAndFamilySymbolName_DoNotBorrowThePublicTypeOrMark()
    {
        var state = State();
        var instance = new Element { Id = 100, CategoryId = -2001160, Name = "Occurrence mark", TypeId = 999 };
        var type = new Element { Id = 500, CategoryId = -2001160, Name = "Type mark" };
        ElementBuilder.ApplyValidatedDistributionTypeReferenceNames([instance, type], state);
        Assert.Equal("Own family symbol", instance.ValidatedDistributionTypeReferenceName);
        Assert.False(instance.ValidatedDistributionTypeReferenceIsExplicit);
        Assert.Equal(999, instance.TypeId);
        Assert.Equal("Occurrence mark", instance.Name);
        Assert.Equal("Type mark", type.Name);
    }

    [Theory]
    [InlineData("footer")] [InlineData("header_size")] [InlineData("class")]
    [InlineData("owner")] [InlineData("zero_owner")] [InlineData("truncated_root")]
    public void InvalidOwningRoot_FailsClosed(string mutation)
    {
        foreach (ushort cls in new ushort[] { 1794, 1829 })
        {
            byte[] block = Block(cls, mutation);
            if (mutation == "footer") block[^1] ^= 1;
            if (mutation == "header_size") block[8] ^= 1;
            if (mutation == "class") block[12] ^= 1;
            if (mutation == "owner") block[0] ^= 1;
            if (mutation == "zero_owner") BitConverter.GetBytes(0).CopyTo(block, 0);
            if (cls == 1794) Assert.Null(TfBlockDeserializer.ExtractFamilyInstanceSymbolId(block, 0, Size(block), cls));
            else Assert.Null(TfBlockDeserializer.ExtractFamilySymbolName(block, 0, Size(block), cls));
        }
    }

    [Theory]
    [InlineData("zero_type")] [InlineData("self_type")] [InlineData("negative_type")]
    public void InvalidSweepTypeId_IsNotATypeLink(string mutation)
    {
        byte[] block = Block(1794, mutation);
        Assert.Null(TfBlockDeserializer.ExtractFamilyInstanceSymbolId(block, 0, Size(block), 1794));
    }

    [Theory]
    [InlineData("null_pointer")] [InlineData("external_pointer")]
    [InlineData("wrong_pointer")] [InlineData("bad_prior_store")]
    [InlineData("truncated_name")] [InlineData("invalid_utf16")]
    [InlineData("empty_name")] [InlineData("blank_name")] [InlineData("nul_name")]
    public void MissingOrMalformedOwnName_DoesNotBecomeTheMark(string mutation)
    {
        byte[] block = Block(1829, mutation);
        Assert.Null(TfBlockDeserializer.ExtractFamilySymbolName(block, 0, Size(block), 1829));
    }

    [Fact]
    public void BoundsAreCheckedBeforeReading()
    {
        byte[] sweep = Block(1794), type = Block(1829);
        Assert.Null(TfBlockDeserializer.ExtractFamilyInstanceSymbolId(sweep, -1, Size(sweep), 1794));
        Assert.Null(TfBlockDeserializer.ExtractFamilyInstanceSymbolId(sweep, 0, uint.MaxValue, 1794));
        Assert.Null(TfBlockDeserializer.ExtractFamilySymbolName(type, int.MaxValue, Size(type), 1829));
        Assert.Null(TfBlockDeserializer.ExtractFamilySymbolName(type, 0, uint.MaxValue, 1829));
        Assert.Null(TfBlockDeserializer.ExtractFamilySymbolName(type, 0, Size(type), 871));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void WalkerOwnerMustMatchTheHeader(bool type)
    {
        ushort cls = type ? (ushort)1829 : (ushort)1794;
        byte[] block = Block(cls); var state = new ParsingState();
        RvtFileReader.RecordFamilySymbolReferenceSource(state, block, 0, Size(block), cls, 999);
        if (type) Assert.Null(state.FamilySymbolNames[999].Name);
        else Assert.Null(state.FamilyInstanceSymbolIds[999].TypeId);
    }

    [Theory]
    [InlineData("duplicate_sweep")] [InlineData("duplicate_type")]
    [InlineData("mixed_symbol_root")]
    [InlineData("malformed_sweep_first")] [InlineData("malformed_type_first")]
    public void DuplicateOrConflictingRoots_CannotBeRepaired(string mode)
    {
        var state = new ParsingState();
        byte[] sweep = Block(1794), type = Block(1829);
        if (mode == "malformed_sweep_first") Record(state, Block(1794, "truncated_root"));
        if (mode == "malformed_type_first") Record(state, Block(1829, "null_pointer"));
        Record(state, sweep); Record(state, type);
        if (mode == "duplicate_sweep") Record(state, Block(1794, "negative_type")); // another copy; a byte-identical one confirms nothing different
        if (mode == "duplicate_type") Record(state, type);
        if (mode == "mixed_symbol_root") RvtFileReader.RecordFamilySymbolReferenceSource(state, type, 0, Size(type), 512, 500);
        var instance = new Element { Id = 100, CategoryId = -2001160 };
        ElementBuilder.ApplyValidatedDistributionTypeReferenceNames([instance, new Element { Id=500, CategoryId=-2001160 }], state);
        Assert.Null(instance.ValidatedDistributionTypeReferenceName);
    }

    [Theory]
    [InlineData("missing_instance")] [InlineData("missing_type")]
    [InlineData("duplicate_instance")] [InlineData("duplicate_type")]
    [InlineData("wrong_instance_category")] [InlineData("wrong_type_category")]
    public void MaterializedOwnerAndTypeMustBeUniqueAndInTheOwnCategory(string mode)
    {
        var state = State();
        var instance = new Element { Id = 100, CategoryId = -2001160 };
        var type = new Element { Id = 500, CategoryId = -2001160 };
        var all = new List<Element> { instance, type };
        if (mode == "missing_instance") all.Remove(instance);
        if (mode == "missing_type") all.Remove(type);
        if (mode == "duplicate_instance") all.Add(instance);
        if (mode == "duplicate_type") all.Add(type);
        if (mode == "wrong_instance_category") instance.CategoryId = -2000011;
        if (mode == "wrong_type_category") type.CategoryId = -2000011;
        ElementBuilder.ApplyValidatedDistributionTypeReferenceNames(all, state);
        Assert.Null(instance.ValidatedDistributionTypeReferenceName);
    }

    [Theory]
    [InlineData("instance", "Occurrence")] [InlineData("type", "Type")]
    [InlineData("type_alias", "Type alias")] [InlineData("expression", null)]
    [InlineData("conflict", null)]
    public void ExplicitReferencePrecedenceIsPreserved(string mode, string? expected)
    {
        var instance = new Element { Id = 100, CategoryId = -2001160 };
        var type = new Element { Id = 500, CategoryId = -2001160 };
        var scope = mode.StartsWith("type") ? type : instance;
        scope.Parameters.Add(new Parameter { Definition = new Definition { Name = mode == "type_alias" ? "Reference[Type]" : "Reference" }, StorageType = StorageType.String,
            Value = mode switch { "instance" => "Occurrence", "type" => "Type", "type_alias" => "Type alias", "expression" => "{Name}", _ => "One" } });
        if (mode == "conflict") scope.Parameters.Add(new Parameter { Definition = new Definition { Name = "Reference" }, StorageType = StorageType.String, Value = "Two" });
        ElementBuilder.ApplyValidatedDistributionTypeReferenceNames([instance,type], State());
        Assert.Equal(expected, instance.ValidatedDistributionTypeReferenceName);
        Assert.Equal(expected == null, instance.ValidatedDistributionTypeReferenceIsBlocked);
        Assert.Equal(expected != null, instance.ValidatedDistributionTypeReferenceIsExplicit);
    }

    [Fact]
    public void PairCorrectionAppliesToTheParameterWalkWithoutChangingTheSharedSchema()
    {
        // The FamilySymbol parameter walk uses the same pair correction as the own-name walk (it reaches the
        // symbol's stores); the shared schema keeps its self-reference.
        byte[] block = Block(1829);
        Assert.NotNull(TfBlockDeserializer.ExtractParams(block, 0, Size(block), 1829));
        Assert.Equal("Own family symbol", TfBlockDeserializer.ExtractFamilySymbolName(block, 0, Size(block), 1829));
        Assert.Equal(1763, TfBlockDeserializer.SerializationFields(1763)!.Single(f => f.Name == "second").Ref);
    }

    [Fact]
    public void ReapplyingWithoutSourceClearsThePreviousValidation()
    {
        var instance = new Element { Id = 100, CategoryId = -2001160 };
        var type = new Element { Id = 500, CategoryId = -2001160 };
        ElementBuilder.ApplyValidatedDistributionTypeReferenceNames([instance, type], State());
        Assert.NotNull(instance.ValidatedDistributionTypeReferenceName);
        ElementBuilder.ApplyValidatedDistributionTypeReferenceNames([instance, type], new ParsingState());
        Assert.Null(instance.ValidatedDistributionTypeReferenceName);
    }

    private static ParsingState State()
    {
        var state = new ParsingState(); Record(state, Block(1794)); Record(state, Block(1829)); return state;
    }
    private static uint Size(byte[] block) => BitConverter.ToUInt32(block, 8);
    private static void Record(ParsingState state, byte[] block) => RvtFileReader.RecordFamilySymbolReferenceSource(
        state, block, 0, Size(block), BitConverter.ToUInt16(block,12), BitConverter.ToUInt32(block,0));

    private static byte[] Block(ushort cls, string? mutation = null, bool project = false)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        int owner = cls == 1794 ? 100 : 500;
        writer.Write(owner); writer.Write(0u); writer.Write(0u); writer.Write(cls);
        var deferred = new List<ushort>();
        foreach (var field in TfBlockDeserializer.SerializationFields(cls)!)
        {
            if (field.Declaring == 37 && field.Name == "m_id") { writer.Write(owner); continue; }
            if (project && field.Declaring == 37 && field.Name == "m_docAccess") { writer.Write(1u); continue; }
            if (mutation == "truncated_root" && field.Name == (cls == 1794 ? "m_masterSymbolId" : "m_subInstData")) break;
            if (field.Declaring == 1794 && field.Name == "m_masterSymbolId")
            { writer.Write(mutation switch { "zero_type" => 0, "negative_type" => -1, "self_type" => owner, _ => 500 }); continue; }
            if (field.Name == "m_pParamValueSetAString" && cls == 1829) { Pointer(2886); continue; }
            if (field.Name == "m_symbolInfo")
            {
                if (mutation == "null_pointer") writer.Write(0u);
                else Pointer(mutation == "external_pointer" ? (ushort)0 : mutation == "wrong_pointer" ? (ushort)3904 : (ushort)3905);
                continue;
            }
            if (field.Name == "m_subInstData")
            {
                // One real pair<int, FamSubInstDescriptor>, not a recursive pair.
                writer.Write(1u); writer.Write(42);
                foreach (var nested in TfBlockDeserializer.SerializationFields(1724)!) WriteDefault(nested);
                continue;
            }
            WriteDefault(field);
        }
        if (mutation != "truncated_root") foreach (ushort deferredClass in deferred)
        {
            if (deferredClass == 2886)
            {
                writer.Write(mutation == "bad_prior_store" ? uint.MaxValue : 1u);
                if (mutation == "bad_prior_store") break;
                writer.Write(11070); WriteString("Wrong mark");
            }
            else if (deferredClass == 3905)
            {
                if (mutation == "truncated_name") writer.Write(999u);
                else if (mutation == "invalid_utf16") { writer.Write(1u); writer.Write((ushort)0xD800); }
                else if (mutation == "empty_name") WriteString("");
                else if (mutation == "blank_name") WriteString(" \t");
                else if (mutation == "nul_name") WriteString("Name\0Other");
                else WriteString("Own family symbol");
            }
        }
        writer.Write(0u);
        byte[] block = stream.ToArray(); uint size = (uint)block.Length - 16;
        BitConverter.GetBytes(size).CopyTo(block, 8); BitConverter.GetBytes(size).CopyTo(block, block.Length - 4);
        return block;
        void Pointer(ushort id) { writer.Write(uint.MaxValue); writer.Write(id); if (id > 0) deferred.Add(id); }
        void WriteString(string value) { writer.Write((uint)value.Length); writer.Write(Encoding.Unicode.GetBytes(value)); }
        void WriteDefault(TfBlockDeserializer.TfField field)
        {
            if (field.Declaring == 297 && field.Name == "m_minmax") { writer.Write(new byte[48]); return; }
            if (field.Mods >> 4 == 5) { writer.Write(0u); return; }
            int count = field.Mods >> 4 == 1 ? field.Cnt!.Value : 1;
            for (int index = 0; index < count; index++) switch (field.Type)
            {
                case 1: case 2: case 3: writer.Write((byte)0); break;
                case 4: case 5: case 6: writer.Write(0); break;
                case 8: writer.Write(0u); break;
                case 9: writer.Write(new byte[16]); break;
                case 10: writer.Write(0L); break;
                case 13: writer.Write(new byte[(field.Cnt ?? 1) * 8]); break;
                case 7: writer.Write(0d); break;
                case 14 when (field.Mods & 15) == 0 && field.Ref is not null and not 20:
                    foreach (var nested in TfBlockDeserializer.SerializationFields(field.Ref.Value)!) WriteDefault(nested); break;
                case 14: writer.Write(field.Ref == 20 ? -1 : 0); break;
                default: throw new InvalidOperationException(field.Name);
            }
        }
    }
}
