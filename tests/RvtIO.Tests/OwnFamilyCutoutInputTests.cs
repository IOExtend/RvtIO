using System.Text.Json;
using RvtIO.BuiltIns;
using RvtIO.Internal;
using RvtIO.Model;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

public class OwnFamilyCutoutInputTests
{
    [Theory]
    [InlineData(0, false)] [InlineData(1, true)] [InlineData(2, true)]
    public void ActiveAndFamilyIdentityAreOwnNamedFields_NotProfileOrNameGuesses(byte active, bool expected)
    {
        byte[] bytes = FamilyCutNativeIndexTests.Block([0, 0], familyId: 999, active: active);
        var info = TfBlockDeserializer.ExtractOwnFamilyCutSymbol(bytes, 0, Size(bytes), 1829)!;
        Assert.Equal(999, info.FamilyId); Assert.Equal(expected, info.Active);
        bytes[^1] ^= 1;
        Assert.Null(TfBlockDeserializer.ExtractOwnFamilyCutSymbol(bytes, 0, Size(bytes), 1829));
    }

    [Theory]
    [InlineData(0, false)] [InlineData(1, true)] [InlineData(2, true)]
    public void InPlaceComesFromInheritedOwnFamilyBaseField(byte value, bool expected)
    {
        byte[] bytes = Family(value);
        Assert.Equal(expected, TfBlockDeserializer.ExtractOwnFamilyInPlace(bytes, 0, Size(bytes), 1772));
        byte[] embedded = new byte[bytes.Length + 19]; bytes.CopyTo(embedded, 7);
        Assert.Equal(expected, TfBlockDeserializer.ExtractOwnFamilyInPlace(embedded, 7, Size(bytes), 1772));
        bytes[^1] ^= 1;
        Assert.Null(TfBlockDeserializer.ExtractOwnFamilyInPlace(bytes, 0, Size(bytes), 1772));
    }

    [Theory]
    [InlineData("valid")] [InlineData("duplicate_occurrence")] [InlineData("duplicate_symbol")]
    [InlineData("missing_original_validation")] [InlineData("mixed_source_root")] [InlineData("mixed_symbol_root")]
    [InlineData("duplicate_instance_root")] [InlineData("nested")]
    [InlineData("no_transform")] [InlineData("inactive")] [InlineData("multiple_loops")]
    [InlineData("inplace_master_family")] [InlineData("missing_master")] [InlineData("mixed_family_root")]
    [InlineData("missing_raw_host")] [InlineData("host_disagrees")]
    [InlineData("nonvertical_insert")] [InlineData("null_nested_count")] [InlineData("duplicate_master_root")]
    public void MaterializationUsesComputedCutButMasterFamilyHostingModeAndRejectsAmbiguity(string mutation)
    {
        var instance = new Element { Id = 100, ValidatedOriginalFamilySymbolId = 600, SerializedHostId = 300 };
        var symbol = new Element { Id = 600 };
        var elements = new List<Element> { instance, symbol };
        var state = new ParsingState();
        double[] matrix = [1, 0, 0, 0, 1, 0, 0, 0, 1, 10, 20, 30];
        var loop = new FamilyCutLoopData([[0, 0, 0], [1, 0, 0], [1, 0, 2], [0, 0, 2]], [0, -1, 0], 5);
        state.ComputedFamilySymbols[100] = (1, new(600, 1, 400, 504)
            { Transform = matrix, SubInstanceCount = 0, HostId = 300, InsertOrientation = 0 });
        state.FamilyInstanceSymbolIds[100] = (1, 500);
        state.ComputedSymbolTargetClasses[100] = (1, 1794);
        state.ComputedSymbolTargetClasses[600] = (1, 1829);
        state.ComputedSymbolTargetClasses[500] = (1, 1829);
        state.ComputedSymbolTargetClasses[200] = (1, 1772);
        // Native getMyCutIdcs examines the master symbol's family, not the computed cut symbol's family.
        state.OwnFamilyCutSymbols[600] = (1, new(201, true), [loop]);
        state.OwnFamilyCutSymbols[500] = (1, new(200, false), null);
        state.OwnFamilyInPlace[200] = (1, false);
        switch (mutation)
        {
            case "duplicate_occurrence": elements.Add(new() { Id = 100 }); break;
            case "duplicate_symbol": elements.Add(new() { Id = 600 }); break;
            case "missing_original_validation": instance.ValidatedOriginalFamilySymbolId = null; break;
            case "mixed_source_root": state.ComputedSymbolTargetClasses[100] = (2, null); break;
            case "mixed_symbol_root": state.ComputedSymbolTargetClasses[600] = (2, null); break;
            case "duplicate_instance_root": state.ComputedFamilySymbols[100] = (2, null); break;
            case "nested": state.ComputedFamilySymbols[100] = (1, state.ComputedFamilySymbols[100].Symbol! with { SubInstanceCount = 1 }); break;
            case "no_transform": state.ComputedFamilySymbols[100] = (1, state.ComputedFamilySymbols[100].Symbol! with { Transform = null }); break;
            case "inactive": state.OwnFamilyCutSymbols[600] = (1, new(201, false), [loop]); break;
            case "multiple_loops": state.OwnFamilyCutSymbols[600] = (1, new(201, true), [loop, loop]); break;
            case "inplace_master_family": state.OwnFamilyInPlace[200] = (1, true); break;
            case "missing_master": state.OwnFamilyCutSymbols.Remove(500); break;
            case "mixed_family_root": state.ComputedSymbolTargetClasses[200] = (2, null); break;
            case "missing_raw_host": state.ComputedFamilySymbols[100] = (1, state.ComputedFamilySymbols[100].Symbol! with { HostId = null }); break;
            case "host_disagrees": instance.SerializedHostId = 400; break;
            case "nonvertical_insert": state.ComputedFamilySymbols[100] = (1, state.ComputedFamilySymbols[100].Symbol! with { InsertOrientation = 1 }); break;
            case "null_nested_count": state.ComputedFamilySymbols[100] = (1, state.ComputedFamilySymbols[100].Symbol! with { SubInstanceCount = null }); break;
            case "duplicate_master_root": state.ComputedSymbolTargetClasses[500] = (2, null); break;
        }
        string before = JsonSerializer.Serialize(elements);
        ElementBuilder.ApplyOwnFamilyCutoutInputs(elements, state);
        if (mutation == "valid")
        {
            var own = instance.OwnSingleFamilyCutout!.Value;
            Assert.Equal(600, own.SymbolId); Assert.Same(matrix, own.Transform); Assert.Same(loop, own.Loop);
        }
        else Assert.Null(instance.OwnSingleFamilyCutout);
        Assert.Null(symbol.OwnSingleFamilyCutout);
        Assert.Equal(before, JsonSerializer.Serialize(elements));
        ElementBuilder.ApplyOwnFamilyCutoutInputs(elements, new ParsingState());
        Assert.Null(instance.OwnSingleFamilyCutout);
    }

    [Theory]
    [InlineData("door", true)] [InlineData("window", false)] [InlineData("no_category", false)]
    [InlineData("no_block", false)] [InlineData("no_symbols", false)] [InlineData("empty_symbols", false)]
    [InlineData("other_symbol_block", false)] [InlineData("not_a_single_occurrence", false)]
    public void OwnDoorPlanSymbolsComeFromTheGRepOfTheSymbolAndTravelWithThePlacementOfTheDoor(string mutation, bool expected)
    {
        var instance = new Element
        {
            Id = 100, ValidatedOriginalFamilySymbolId = 600, SerializedHostId = 300,
            CategoryId = mutation switch
            {
                "window" => (int)BuiltInCategory.OST_Windows,
                "no_category" => null,
                _ => (int)BuiltInCategory.OST_Doors,
            },
        };
        var elements = new List<Element> { instance, new() { Id = 600 } };
        double[] matrix = [1, 0, 0, 0, 1, 0, 0, 0, 1, 10, 20, 30];
        var state = new ParsingState();
        state.ComputedFamilySymbols[100] = (1, new(600, 1, 400, 504)
            { Transform = matrix, SubInstanceCount = 0, HostId = 300, InsertOrientation = 0 });
        state.ComputedSymbolTargetClasses[100] = (1, 1794);
        state.ComputedSymbolTargetClasses[600] = (1, 1829);
        IReadOnlyList<DoorPlanSymbol> symbols = [new DoorPlanSymbol(null, [new GRepParserV2.ArcRec()])];
        switch (mutation)
        {
            case "no_block": break;
            case "no_symbols": state.GrepBlocks[600] = new GRepData(); break;
            case "empty_symbols": state.GrepBlocks[600] = new GRepData { DoorPlanSymbols = [] }; break;
            // The symbols belong to the symbol of the door, not to the door element or to another symbol.
            case "other_symbol_block": state.GrepBlocks[100] = new GRepData { DoorPlanSymbols = symbols }; break;
            default: state.GrepBlocks[600] = new GRepData { DoorPlanSymbols = symbols }; break;
        }
        // A value from an earlier run must not survive an element that no longer qualifies.
        instance.OwnDoorPlanSymbols = ([new DoorPlanSymbol(null, [])], new double[12]);
        if (mutation == "not_a_single_occurrence") elements.Add(new() { Id = 100 });

        ElementBuilder.ApplyOwnFamilyCutoutInputs(elements, state);

        if (expected)
        {
            var own = instance.OwnDoorPlanSymbols!.Value;
            Assert.Same(symbols, own.Symbols);
            Assert.Same(matrix, own.Transform);
            Assert.Null(instance.OwnSingleFamilyCutout);
        }
        else Assert.Null(instance.OwnDoorPlanSymbols);
    }

    [Theory]
    [InlineData("duplicate")] [InlineData("foreign-owner")] [InlineData("invalid-first")]
    public void RecordingNeverRepairsAnAmbiguousOwnSymbol(string mutation)
    {
        var state = new ParsingState(); byte[] bytes = FamilyCutNativeIndexTests.Block([0, 0]);
        if (mutation == "duplicate") Record(bytes);
        if (mutation == "invalid-first")
        { byte[] invalid = (byte[])bytes.Clone(); invalid[^1] ^= 1; Record(invalid); }
        Record(bytes);
        var own = state.OwnFamilyCutSymbols[mutation == "foreign-owner" ? 999u : 100u];
        Assert.Equal(mutation == "foreign-owner" ? 1 : 2, own.BlockCount);
        Assert.Null(own.Symbol); Assert.Null(own.Loops);
        void Record(byte[] block) => RvtFileReader.RecordOwnFamilyCutoutInputs(state, block, 0, Size(block), 1829,
            mutation == "foreign-owner" ? 999u : 100u);
    }

    private static uint Size(byte[] bytes) => BitConverter.ToUInt32(bytes, 8);

    private static byte[] Family(byte inPlace)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write(200); writer.Write(0u); writer.Write(0u); writer.Write((ushort)1772);
        foreach (var field in TfBlockDeserializer.SerializationFields(1772)!)
        {
            if (field.Declaring == 37 && field.Name == "m_id") { writer.Write(200); continue; }
            if (field.Declaring == 37 && field.Name == "m_docAccess") { writer.Write(1u); continue; }
            if (field.Declaring == 1773 && field.Name == "m_inPlace") { writer.Write(inPlace); break; }
            Default(field);
        }
        writer.Write(0u); byte[] bytes = stream.ToArray(); uint size = (uint)bytes.Length - 16;
        BitConverter.GetBytes(size).CopyTo(bytes, 8); BitConverter.GetBytes(size).CopyTo(bytes, bytes.Length - 4);
        return bytes;
        void Default(TfBlockDeserializer.TfField field)
        {
            if (field.Declaring == 297 && field.Name == "m_minmax") { writer.Write(new byte[48]); return; }
            if (field.Mods >> 4 is 5 or 6) { writer.Write(0u); return; }
            int count = field.Mods >> 4 == 1 ? field.Cnt!.Value : 1;
            for (int index = 0; index < count; index++) switch (field.Type)
            {
                case 1: case 2: case 3: writer.Write((byte)0); break;
                case 4: case 5: case 6: case 8: writer.Write(0); break;
                case 7: writer.Write(0d); break;
                case 9: writer.Write(new byte[16]); break;
                case 10: writer.Write(0L); break;
                case 13: writer.Write(new byte[(field.Cnt ?? 1) * 8]); break;
                case 14 when (field.Mods & 15) == 0 && field.Ref is not null and not 20:
                    foreach (var nested in TfBlockDeserializer.SerializationFields(field.Ref.Value)!) Default(nested); break;
                case 14: writer.Write(field.Ref == 20 ? -1 : 0); break;
                default: throw new InvalidOperationException(field.Name);
            }
        }
    }
}
