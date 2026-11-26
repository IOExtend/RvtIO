using RvtIO.Internal;
using RvtIO.Model;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

public class StairElementSerializationTests
{
    [Fact]
    public void ShallowCopyKeepsIndependentStairPropertiesAndSharesTheirContents()
    {
        var source = new Element
        {
            SerializedStairCopyLevelIds = [1, 2],
            SerializedStairTypeId = 0,
            SerializedStairTypeLinkIsResolved = true,
            SerializedActualRiserHeightFeet = 0.0,
        };
        var copy = source.ShallowCopy();
        Assert.Same(source.SerializedStairCopyLevelIds, copy.SerializedStairCopyLevelIds);
        Assert.Equal(0, copy.SerializedStairTypeId);
        Assert.Equal(0.0, copy.SerializedActualRiserHeightFeet);

        ElementBuilder.ApplySerializedStairData([copy], new Dictionary<uint, ExactTfRootSource>(),
            new Dictionary<uint, ExactTfRootSource>());
        Assert.Null(copy.SerializedStairTypeId);
        Assert.False(copy.SerializedStairTypeLinkIsResolved);
        Assert.Null(copy.SerializedActualRiserHeightFeet);
        Assert.Same(source.SerializedStairCopyLevelIds, copy.SerializedStairCopyLevelIds);
        copy.SerializedStairCopyLevelIds = null;
        Assert.NotNull(source.SerializedStairCopyLevelIds);
        Assert.Equal(0, source.SerializedStairTypeId);
        Assert.True(source.SerializedStairTypeLinkIsResolved);
        Assert.Equal(0.0, source.SerializedActualRiserHeightFeet);
    }

    internal const string ExactStairBlockBase64 =
        "5tQHABqGDfu3CAAAYQ4AAAAAAAAAAAAAAAAAAAAA/////yYI/////ycIAAAAAP////+NAgEAAADm1AcA/////////////////////7FTAQD//////////wAAAAAAAAAAAAAAAQAAAP////+qAwIAAAD/////qgP/////qgMAAAAA8L3LC7r04j/g7/f7/X7vPwAAAAAAAAAAOPFNfBPf/D8AAAAAAAAAAAMAAADG3AcA/////zcBAAABAAAAtSkCAAABAQABAAAA/////2UOAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAgAAAAIAAAACAAAAAgAAAAIAAAACAAAAAwAAAAv//////////wAMAAAAAgAAAAIAAAACAAAAAgAAAAIAAAACAAAAAgAAAAIAAAACAAAAAgAAAAIAAAACAAAAAAAAAAAAAAAAAAAAAwAAAP////9iDv////9tA/////9kDgAEAAAAAwAAAMgGBAAAAMgGBQAAAMgGBgAAAMgGAQEAAAAHAAAAyAYBAQAAAAgAAADIBgAAAAAMAAAACgAAAAIAAAAAAAAAAAAAAAAAAAD/////AgAAAAIAAAAAAAAAAAAAAAAAAAAAAAAAAwAAAAIAAAAAAAAAAAAAAAAAAAABAAAABAAAAAIAAAAAAAAAAAAAAAAAAAACAAAABQAAAAIAAAAAAAAAAAAAAAAAAAADAAAACAAAAAIAAAAAAAAAAAAAAAAAAQD/////CQAAAAIAAAAAAAAAAAAAAAAAAgD/////CwAAAAIAAAAAAAAAAAAAAAAAAwD/////BwAAAAIAAAABAAAAAAAAAAAAAAD/////AAAAAAIAAAABAAAAAAAAAAAAAAAAAAAABgAAAAIAAAABAAAAAAAAAAAAAQD/////AQAAAAIAAAABAAAAAQAAAAAAAAAAAAAAAAAAAAAAAAACAAAAAAAAAH2fCwACAAAA/////1oAAwAAAAEAAAAd1QcAAAAAAAAAAAAAAAAAEQAAAAAAAAAAAAAAAAAAAAEAAACAlpgAAAAAAObUBwD/////GgAAAP////8AAAAA/////wBAifD/KIYBAAAAAAAAAAAAAAAAAAEAAACAlpgAAAAAAObUBwD/////JAAAAP////8AAAAA/////wBAifD/KIYBAAAAAAAAAAAAAAAAAAEAAACAlpgAAAAAAObUBwD/////JwAAAP////8AAAAA/////wBAifD/KIYBAAAAAAAAAAAAAAAAAAEAAACAlpgAAAAAAObUBwD/////KQAAAP////8AAAAA/////wBAifD/KIYBAAAAAAAAAAAAAAAAAAEAAACAlpgAAAAAAObUBwD/////RgAAAP////8AAAAA/////wBAifD/KIYBAAAAAAAAAAAAAAAAAAEAAACAlpgAAAAAAObUBwD/////UAAAAP////8AAAAA/////wBAifD/KIYBAAAAAAAAAAAAAAAAAAEAAACAlpgAAAAAAObUBwD/////UwAAAP////8AAAAA/////wBAifD/KIYBAAAAAAAAAAAAAAAAAAEAAACAlpgAAAAAAObUBwD/////VQAAAP////8AAAAA/////wBAifD/KIYBAAAAAAAAAAAAAAAAAAEAAACAlpgAAAAAAObUBwD/////ggAAAP////8AAAAA/////wBAifD/KIYBAAAAAAAAAAAAAAAAAAEAAACAlpgAAAAAAObUBwD/////jAAAAP////8AAAAA/////wBAifD/KIYBAAAAAAAAAAAAAAAAAAEAAACAlpgAAAAAAObUBwD/////jwAAAP////8AAAAA/////wBAifD/KIYBAAAAAAAAAAAAAAAAAAEAAACAlpgAAAAAAObUBwD/////kQAAAP////8AAAAA/////wBAifD/KIYBAAAAAAAAAAAAAAAAAAEAAACAlpgAAAAAAObUBwD/////kwAAAP////8AAAAA/////wBAifD/KIYBAAAAAAAAAAAAAAAAAAEAAACAlpgAAAAAAObUBwD/////iwEAAP////8AAAAA/////wBAifD/KIYBAAAAAAAAAAAAAAAAAAEAAACAlpgAAAAAAObUBwD/////sAEAAP////8AAAAA/////wBAifD/KIYBAAAAAAAAAAAAAAAAAAEAAACAlpgAAAAAAObUBwD/////IgIAAP////8AAAAA/////wBAifD/KIYBAAAAAAAAAAAAAAAAAAEAAACAlpgAAAAAAObUBwD/////LgIAAP////8AAAAA/////wBAifD/KIYBAAEAAAAAAAAAAAAIAAAAAAAAAP////8EAAgBAAAAAAAADD0E9Pl8Pp8HQPGDqVhTbUVA+LSMyljYJsA48U18E9/8v0b/2ODJouU8AAAAAAAA8D8AAAAAAAAAAAkAAAAAAAAA/////wQACAHip/6pf2oTwOKn/ql/ahNA9q5pYwMAQ0D3N04rifAgwDjxTXwT3/y/AAAAAAAA8L8AAAAAAAAAgAAAAAAAAACACgAAAAAAAAD/////BAAIAczz+Xw+nwfAAAAAAAAADD352Slus5JAQOq0jMpY2CbAOPFNfBPf/L9G/9jgyaLlvAAAAAAAAPC/AAAAAAAAAIALAAAAAAAAAP////8EAAgB4qf+qX9qE8Dip/6pf2oTQPWuaWMDAENA8bSMyljYJsA48U18E9/8vwAAAAAAAPA/AAAAAAAAAAAAAAAAAAAAAAYAAAAAAAAA/////wQACAEAAAAAAAAAAC6YS4wBkgtA8YOpWFNtRUDxtIzKWNgmwDjxTXwT3/y/QWrPWBiS4jx0Z8lY12rrP1u8b3VNgOA/BwAAAAAAAAD/////BAAIAS6YS4wBkgvAAAAAAAAAAID52Slus5JAQPG0jMpY2CbAOPFNfBPf/L9Bas9YGJLivHRnyVjXauu/W7xvdU2A4L8BAAAAHdUHAAAAAAC3CAAA";
    private const string ExactStairsTypeBlockBase64 =
        "tSkCALpyDdY5AQAAng4AAAAA/////0kLAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAQAAALUpAgD/////////////////////////////////////AAAA/////0EP///////////ACrACrADjP1lX1pV1Ze0/AAAAAAAAAACP8qP8KD8KQMwQM8QMMQFAzbwz78w7/z8yxAwxQ8wAQED1T/1T/9Q/QPVP/VP/1D8AAAAAAAAAADPEDDFDzKA/M8QMMUPMoD8AAAAAAAAAAAAAAAAAAAAAmJ4CAJeeAgD//////////////////////////5spAgABAAAAAgAAAAEAAAAAAAAAAQAAAAAAAAAAAAAAAAAAAAIAAAAAAAEAAADSufD/AAAAAA0AAABQAHIAZQBjAGEAcwB0ACAAUwB0AGEAaQByADkBAAA=";

    [Fact]
    public void ExactClass3681Block_CapturesCompleteNamedRoot()
    {
        byte[] block = Convert.FromBase64String(ExactStairBlockBase64);
        uint dataSize = BitConverter.ToUInt32(block, 8);

        Assert.True(TfBlockDeserializer.TryExtractExactStairElementRoot(
            block, 0, dataSize, 3681, out var root));
        Assert.Equal(513254, root.Fields["m_id"]);
        Assert.Equal(141749, root.Fields["m_typeId"]);
        Assert.Equal(3, root.Fields["m_actualNumberOfRisers"]);
        Assert.Equal(0.5923738699329402,
            root.DoubleFields["m_actualRiserHeight"]);
        Assert.Equal(0.984251968503937,
            root.DoubleFields["m_actualTreadDepth"]);
    }

    [Fact]
    public void ExactClass3742Block_CapturesTypeOwnerRoot()
    {
        byte[] block = Convert.FromBase64String(ExactStairsTypeBlockBase64);
        uint dataSize = BitConverter.ToUInt32(block, 8);

        Assert.True(TfBlockDeserializer.TryExtractExactStairsTypeRoot(
            block, 0, dataSize, 3742, out var root));
        Assert.Equal(141749, root.Fields["m_id"]);
    }

    [Theory]
    [InlineData("header_size")]
    [InlineData("full_class")]
    [InlineData("owner")]
    [InlineData("footer")]
    [InlineData("truncated")]
    [InlineData("low_class")]
    public void MalformedClass3681Block_FailsClosed(string mismatch)
    {
        byte[] block = Convert.FromBase64String(ExactStairBlockBase64);
        uint dataSize = BitConverter.ToUInt32(block, 8);
        ushort classId = 3681;
        switch (mismatch)
        {
            case "header_size": block[8] ^= 1; break;
            case "full_class": block[14] = 1; break;
            case "owner": block[0] ^= 1; break;
            case "footer": block[^1] ^= 1; break;
            case "truncated": Array.Resize(ref block, block.Length - 1); break;
            case "low_class": classId = 3708; break;
        }

        Assert.False(TfBlockDeserializer.TryExtractExactStairElementRoot(
            block, 0, dataSize, classId, out _));
    }

    [Fact]
    public void StairRootRecorder_DuplicatePermanentlyClearsExactRoot()
    {
        byte[] block = Convert.FromBase64String(ExactStairBlockBase64);
        uint dataSize = BitConverter.ToUInt32(block, 8);
        uint ownerId = BitConverter.ToUInt32(block, 0);
        var state = new ParsingState();

        RvtFileReader.RecordStairRootSource(
            state, block, 0, dataSize, 3681, ownerId);
        Assert.Equal(1, state.StairElementRootSource[ownerId].BlockCount);
        Assert.NotNull(state.StairElementRootSource[ownerId].Root);

        RvtFileReader.RecordStairRootSource(
            state, block, 0, dataSize, 3681, ownerId);
        Assert.Equal(2, state.StairElementRootSource[ownerId].BlockCount);
        Assert.Null(state.StairElementRootSource[ownerId].Root);
    }

    [Fact]
    public void StairRootRecorder_MalformedMatchRetainsCountWithoutRoot()
    {
        byte[] block = Convert.FromBase64String(ExactStairBlockBase64);
        uint dataSize = BitConverter.ToUInt32(block, 8);
        uint ownerId = BitConverter.ToUInt32(block, 0);
        block[14] = 1;
        var state = new ParsingState();

        RvtFileReader.RecordStairRootSource(
            state, block, 0, dataSize, 3681, ownerId);

        Assert.Equal(1, state.StairElementRootSource[ownerId].BlockCount);
        Assert.Null(state.StairElementRootSource[ownerId].Root);
    }

    [Fact]
    public void StairData_TransfersOnlyCompleteNamedClass3681FieldsAndTypeLink()
    {
        const int stairId = 513254;
        const int typeId = 141749;
        var stair = new Element { Id = stairId, CategoryId = -2000120 };
        var type = StairType(typeId);
        var native = StairNativeParams(
            stairId, typeId, 3,
            0.5923738699329402, 0.984251968503937);

        ElementBuilder.ApplySerializedStairData(
            [stair, type],
            new Dictionary<uint, ExactTfRootSource>
            {
                [(uint)stairId] = new(1, native),
            },
            new Dictionary<uint, ExactTfRootSource>
            {
                [(uint)typeId] = new(1, OwnerParams(typeId)),
            });

        Assert.Equal(typeId, stair.SerializedStairTypeId);
        Assert.True(stair.SerializedStairTypeLinkIsResolved);
        Assert.Equal((ushort)3742, type.SerializedStairsTypeClassId);
        Assert.Equal(3, stair.SerializedActualNumberOfRisers);
        Assert.Equal(
            0.5923738699329402, stair.SerializedActualRiserHeightFeet);
        Assert.Equal(
            0.984251968503937, stair.SerializedActualTreadDepthFeet);
    }

    [Theory]
    [InlineData("class")]
    [InlineData("owner")]
    [InlineData("duplicate_root")]
    [InlineData("duplicate_type_root")]
    [InlineData("missing_type")]
    [InlineData("ambiguous_type")]
    [InlineData("non_stair")]
    public void StairData_RejectsIncompleteOrAmbiguousProvenance(string mismatch)
    {
        const int stairId = 513254;
        const int typeId = 141749;
        var stair = new Element
        {
            Id = stairId,
            CategoryId = mismatch == "non_stair" ? -2000919 : -2000120,
        };
        var native = StairNativeParams(
            mismatch == "owner" ? stairId + 1 : stairId,
            typeId, 3, 0.5, 1.0);
        var type = StairType(typeId);
        IReadOnlyList<Element> elements = mismatch switch
        {
            "missing_type" => [stair],
            "ambiguous_type" => [stair, type, StairType(typeId)],
            _ => [stair, type],
        };
        var occurrenceSource = new Dictionary<uint, ExactTfRootSource>();
        if (mismatch != "class")
        {
            occurrenceSource[(uint)stairId] = mismatch == "duplicate_root"
                ? new(2, null)
                : new(1, native);
        }
        var typeSource = new Dictionary<uint, ExactTfRootSource>();
        if (mismatch != "missing_type")
            typeSource[(uint)typeId] = mismatch == "duplicate_type_root"
                ? new(2, null)
                : new(1, OwnerParams(typeId));

        ElementBuilder.ApplySerializedStairData(
            elements, occurrenceSource, typeSource);

        Assert.Null(stair.SerializedStairTypeId);
        Assert.False(stair.SerializedStairTypeLinkIsResolved);
        Assert.Null(stair.SerializedActualNumberOfRisers);
        Assert.Null(stair.SerializedActualRiserHeightFeet);
        Assert.Null(stair.SerializedActualTreadDepthFeet);
    }

    [Fact]
    public void StairData_InvalidOneNamedValue_OmitsOnlyThatValue()
    {
        const int stairId = 513254;
        const int typeId = 141749;
        var stair = new Element { Id = stairId, CategoryId = -2000120 };
        var type = StairType(typeId);
        var native = StairNativeParams(
            stairId, typeId, -1, double.NaN, 0.984251968503937);

        ElementBuilder.ApplySerializedStairData(
            [stair, type],
            new Dictionary<uint, ExactTfRootSource>
            {
                [(uint)stairId] = new(1, native),
            },
            new Dictionary<uint, ExactTfRootSource>
            {
                [(uint)typeId] = new(1, OwnerParams(typeId)),
            });

        Assert.Equal(typeId, stair.SerializedStairTypeId);
        Assert.True(stair.SerializedStairTypeLinkIsResolved);
        Assert.Null(stair.SerializedActualNumberOfRisers);
        Assert.Null(stair.SerializedActualRiserHeightFeet);
        Assert.Equal(
            0.984251968503937, stair.SerializedActualTreadDepthFeet);
    }

    private static TfBlockDeserializer.NativeParams StairNativeParams(
        int ownerId, int typeId, int numberOfRisers,
        double riserHeight, double treadDepth)
    {
        var native = new TfBlockDeserializer.NativeParams();
        native.Fields["m_id"] = ownerId;
        native.Fields["m_typeId"] = typeId;
        native.Fields["m_actualNumberOfRisers"] = numberOfRisers;
        native.DoubleFields["m_actualRiserHeight"] = riserHeight;
        native.DoubleFields["m_actualTreadDepth"] = treadDepth;
        return native;
    }

    private static TfBlockDeserializer.NativeParams OwnerParams(int ownerId)
    {
        var native = new TfBlockDeserializer.NativeParams();
        native.Fields["m_id"] = ownerId;
        return native;
    }

    private static Element StairType(int id) => new()
    {
        Id = id,
        CategoryId = -2000120,
        Category = "OST_Stairs",
    };
}
