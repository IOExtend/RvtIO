using System.Text;
using RvtIO.Internal;
using RvtIO.Model;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

public class RoomNameSerializationTests
{
    [Theory]
    [InlineData(7)]
    [InlineData(-1)]
    public void Metadata_UsesRoomPhaseNotCreatedPhase(int phase)
    {
        byte[] block = Block(phase: phase);
        Assert.True(TfBlockDeserializer.TryExtractExactRoomMetadata(block, 0, Size(block), 3471,
            out _, out int? actual));
        Assert.Equal(phase, actual);
        var state = new ParsingState();
        RvtFileReader.RecordRoomNameSource(state, block, 0, Size(block), 3471, 100);
        Assert.Equal(phase > 0, state.ExactRoomPhaseIds.ContainsKey(100));
        RvtFileReader.RecordRoomNameSource(state, block, 0, Size(block), 3471, 100);
        Assert.False(state.ExactRoomPhaseIds.ContainsKey(100));
    }

    [Fact]
    public void Metadata_KeepsTheRoomPhaseOfZeroOfASchemaReadFile()
    {
        byte[] block = Block(phase: 0);
        var state = new ParsingState { WideSchema = new FileSchema(new SchemaDocument()) };
        RvtFileReader.RecordRoomNameSource(state, block, 0, Size(block), 3471, 100);
        Assert.Equal(0, state.ExactRoomPhaseIds[100]);
        // The Revit 2022 layout numbers phases from 1.
        var narrow = new ParsingState();
        RvtFileReader.RecordRoomNameSource(narrow, block, 0, Size(block), 3471, 100);
        Assert.False(narrow.ExactRoomPhaseIds.ContainsKey(100));
        // A second block of the same owner discards the validation.
        RvtFileReader.RecordRoomNameSource(state, block, 0, Size(block), 3471, 100);
        Assert.False(state.ExactRoomPhaseIds.ContainsKey(100));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Metadata_ReadsTheLocationlessFlagOfAPlacedOrUnplacedRoom(bool locationless)
    {
        byte[] block = Block(locationless: locationless);
        Assert.True(TfBlockDeserializer.TryExtractExactRoomMetadata(block, 0, Size(block), 3471,
            out _, out _, out bool? actual));
        Assert.Equal(locationless, actual);
        var state = new ParsingState();
        RvtFileReader.RecordRoomNameSource(state, block, 0, Size(block), 3471, 100);
        Assert.Equal(locationless, state.ExactRoomLocationless[100]);
        var room = new Element { Id = 100, CategoryId = -2000160 };
        var area = new Element { Id = 101, CategoryId = -2003200 };
        state.RoomNameSource[101] = (1, "Area"); state.ExactRoomLocationless[101] = true;
        var info = new RevitFileInfo { Elements = [room, area] };
        ElementBuilder.ApplySerializedSpatialPhases(info, state);
        Assert.Equal(locationless, room.SerializedRoomIsLocationless);
        Assert.Null(area.SerializedRoomIsLocationless);
        // A second block of the same owner discards the validation.
        RvtFileReader.RecordRoomNameSource(state, block, 0, Size(block), 3471, 100);
        Assert.False(state.ExactRoomLocationless.ContainsKey(100));
    }

    [Fact]
    public void CurrentRoom_UsesItsFramedRootWhenTheStreamScanCannotAnchor()
    {
        byte[] room = Block(circuit: 49);
        byte[] chunk = [.. room, .. new byte[16]];
        var state = new ParsingState
        {
            AllKnownIds = [100],
            Chunks = [chunk],
            FramedPartitions = [new PartitionFraming.Framed([chunk], [new(102, 100, 0, room.Length)], 4, 2)],
            IncrementRoutedPartitions = new Dictionary<ulong, int> { [100] = 2 },
            StaleCopiesRemoved = true,
        };

        RvtFileReader.RunTfStreamWalk(state);

        Assert.Equal((1, "Room's own name, №2"), state.RoomNameSource[100]);
        Assert.Equal(49, state.ExactRoomCircuitIds[100]);
        Assert.Contains((-1006901, "1.20"), state.NativeParams[100].Strings);
    }

    [Fact]
    public void Metadata_ReadsTheCachedCircuitIdOfTheRoom()
    {
        byte[] block = Block(circuit: 49);
        Assert.True(TfBlockDeserializer.TryExtractExactRoomMetadata(block, 0, Size(block), 3471,
            out _, out _, out _, out int? circuit));
        Assert.Equal(49, circuit);
        var state = new ParsingState();
        RvtFileReader.RecordRoomNameSource(state, block, 0, Size(block), 3471, 100);
        Assert.Equal(49, state.ExactRoomCircuitIds[100]);
        // A second block of the same owner discards the validation.
        RvtFileReader.RecordRoomNameSource(state, block, 0, Size(block), 3471, 100);
        Assert.False(state.ExactRoomCircuitIds.ContainsKey(100));
    }

    [Fact]
    public void Metadata_KeepsTheCircuitOfARoomWithoutAStoredName()
    {
        byte[] block = Block(mutation: "missing", circuit: 49);
        Assert.False(TfBlockDeserializer.TryExtractExactRoomMetadata(block, 0, Size(block), 3471,
            out string? name, out _, out _, out int? circuit));
        Assert.Null(name);
        Assert.Equal(49, circuit);
        var state = new ParsingState();
        RvtFileReader.RecordRoomNameSource(state, block, 0, Size(block), 3471, 100);
        Assert.Equal(49, state.ExactRoomCircuitIds[100]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(13)]
    public void ExactRoomName_ReadsCompleteStoresAfterBoundingItems(int boundingItems)
    {
        byte[] block = Block(boundingItems: boundingItems);
        Assert.True(Decode(block, out var name));
        Assert.Equal("Room's own name, №2", name);
        // The generic walk reads RoomBoundingItem.m_linkRef as the bare ElementId it is (it used to recurse into the
        // schema's self-reference and drop the whole root), so the stored parameters of such rooms are available too.
        Assert.NotNull(TfBlockDeserializer.ExtractParams(block, 0, Size(block), 3471));
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("class")]
    [InlineData("footer")]
    [InlineData("size")]
    [InlineData("truncated")]
    public void ExactRoomName_RejectsInvalidFramingOrOwner(string mutation)
    {
        byte[] block = Block();
        switch (mutation)
        {
            case "owner": BitConverter.GetBytes(200u).CopyTo(block, 0); break;
            case "class": BitConverter.GetBytes((ushort)3473).CopyTo(block, 12); break;
            case "footer": block[^1] ^= 1; break;
            case "size": block[8] ^= 1; break;
            case "truncated": block = block[..^1]; break;
        }
        Assert.False(Decode(block, out var name));
        Assert.Null(name);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("empty")]
    [InlineData("wrong-tag")]
    [InlineData("incomplete-store")]
    [InlineData("wrong-storage")]
    [InlineData("late-truncated")]
    public void ExactRoomName_DoesNotAcceptPartialOrAmbiguousStores(string mutation)
    {
        Assert.False(Decode(Block(mutation: mutation), out var name));
        Assert.Null(name);
    }

    [Fact]
    public void Recorder_DuplicateOrMalformedRootCannotBeOverwrittenByLaterSuccess()
    {
        byte[] block = Block();
        var state = new ParsingState();
        RvtFileReader.RecordRoomNameSource(state, block, 0, Size(block), 3471, 100);
        Assert.Equal((1, "Room's own name, №2"), state.RoomNameSource[100]);
        RvtFileReader.RecordRoomNameSource(state, block, 0, Size(block), 3471, 100);
        Assert.Equal(2, state.RoomNameSource[100].BlockCount);
        Assert.Null(state.RoomNameSource[100].Name);
        var malformed = new ParsingState();
        RvtFileReader.RecordRoomNameSource(malformed, block[..^1], 0, Size(block), 3471, 100);
        RvtFileReader.RecordRoomNameSource(malformed, block, 0, Size(block), 3471, 100);
        Assert.Null(malformed.RoomNameSource[100].Name);
    }

    [Fact]
    public void Builder_OnlyOverridesRoomsWithOneExactNameAndSynchronizesParameter()
    {
        var room = new Element { Id = 100, CategoryId = -2000160, Name = "scan noise" };
        room.Parameters.Add(new Parameter { Id = -1006900, Value = "scan noise", IsApproximate = true });
        var other = new Element { Id = 101, CategoryId = -2000011, Name = "unchanged" };
        var duplicate = new Element { Id = 102, CategoryId = -2000160, Name = "unchanged" };
        var missing = new Element { Id = 103, CategoryId = -2000160, Name = "unchanged" };
        var source = new Dictionary<uint, (int, string?)>
        {
            [100] = (1, "Kitchen"), [101] = (1, "Kitchen"),
            [102] = (2, "Kitchen"), [103] = (1, null),
        };
        ElementBuilder.ApplySerializedRoomNames([room, other, duplicate, missing], source);
        Assert.Equal("Kitchen", room.Name);
        Assert.Equal("Kitchen", room.Parameters[0].Value);
        Assert.Equal(StorageType.String, room.Parameters[0].StorageType);
        Assert.False(room.Parameters[0].IsApproximate);
        Assert.All(new[] { other, duplicate, missing }, e => Assert.Equal("unchanged", e.Name));
    }

    private static uint Size(byte[] block) => BitConverter.ToUInt32(block, 8);
    private static bool Decode(byte[] block, out string? name) =>
        TfBlockDeserializer.TryExtractExactRoomName(block, 0, Size(block), 3471, out name);

    private static byte[] Block(int boundingItems = 1, string? mutation = null, int phase = 7, bool locationless = false,
        int circuit = 0)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.Unicode, leaveOpen: true);
        writer.Write(100u); writer.Write(0u); writer.Write(0u); writer.Write((ushort)3471);
        foreach (var field in TfBlockDeserializer.SerializationFields(3471)!)
        {
            switch (field.Name)
            {
                case "m_pParamValueSetDouble":
                    writer.Write(uint.MaxValue); writer.Write((ushort)2887); continue;
                case "m_pParamValueSetAString":
                    writer.Write(uint.MaxValue); writer.Write((ushort)(mutation == "wrong-tag" ? 2889 : 2886)); continue;
                case "m_pParamValueSetElementId" when mutation == "late-truncated":
                    writer.Write(uint.MaxValue); writer.Write((ushort)2888); continue;
                case "m_id" when field.Declaring == 37: writer.Write(100); continue;
                case "m_phaseId" when field.Declaring == 3471: writer.Write(phase); continue;
                case "m_createdPhaseId": writer.Write(999); continue;
                case "m_cachedCircuitId" when field.Declaring == 3471: writer.Write(circuit); continue;
                case "m_bIsLocationless" when field.Declaring == 3471: writer.Write((byte)(locationless ? 1 : 0)); continue;
                case "m_volumeBoundingElems":
                    writer.Write((uint)boundingItems);
                    for (int i = 0; i < boundingItems; i++) { writer.Write(900 + i); writer.Write(i % 2 == 0 ? -1 : 1000 + i); }
                    continue;
            }
            if ((field.Mods >> 4) == 5) { writer.Write(0u); continue; }
            if ((field.Mods >> 4) == 1)
            {
                Assert.Equal((byte)7, field.Type);
                for (int i = 0; i < field.Cnt; i++) writer.Write(0d);
                continue;
            }
            switch (field.Type)
            {
                case 1: writer.Write((byte)0); break;
                case 4: writer.Write(0); break;
                case 7: writer.Write(0d); break;
                case 14: writer.Write(field.Ref == 20 ? -1 : 0); break;
                default: throw new InvalidOperationException(field.Name);
            }
        }
        writer.Write(1u); writer.Write(2.5d); writer.Write(mutation == "wrong-storage" ? -1006900 : -1006905);
        writer.Write(mutation == "duplicate" ? 3u : mutation == "incomplete-store" ? 10u : 2u);
        WriteString(-1006901, "1.20");
        WriteString(mutation == "missing" ? -1006907 : -1006900,
            mutation == "empty" ? "" : "Room's own name, №2");
        if (mutation == "duplicate") WriteString(-1006900, "conflicting name");
        if (mutation == "late-truncated") { writer.Write(1u); writer.Write(-1001952); }
        writer.Write(0u);
        byte[] block = stream.ToArray();
        uint size = (uint)block.Length - 16;
        BitConverter.GetBytes(size).CopyTo(block, 8);
        BitConverter.GetBytes(size).CopyTo(block, block.Length - 4);
        return block;

        void WriteString(int id, string value)
        {
            writer.Write(id); writer.Write((uint)value.Length); writer.Write(Encoding.Unicode.GetBytes(value));
        }
    }

    [Fact]
    public void ApplyRoomParams_SynthesizesRoomLevelIdFromLevelParamWhenReferencesNull()
    {
        var room = new Element
        {
            Id = 100,
            CategoryId = -2000160,
            References = null,
            Parameters =
            [
                BipHelpers.MakeBipParam(-1001952, "LEVEL_PARAM", StorageType.ElementId, 555)
            ]
        };
        var info = new RevitFileInfo { Elements = [room] };
        ElementBuilder.ApplyRoomParams(info);

        var levelParam = room.Parameters.FirstOrDefault(p => p.Id == -1006916);
        Assert.NotNull(levelParam);
        Assert.Equal("ROOM_LEVEL_ID", levelParam.Definition.Name);
        Assert.Equal(555, levelParam.Value);
        Assert.Equal(StorageType.ElementId, levelParam.StorageType);

        var upperLevelParam = room.Parameters.FirstOrDefault(p => p.Id == -1006922);
        Assert.NotNull(upperLevelParam);
        Assert.Equal("ROOM_UPPER_LEVEL", upperLevelParam.Definition.Name);
        Assert.Equal(555, upperLevelParam.Value);
    }

    [Fact]
    public void ApplyRoomParams_PreservesExistingUpperLevelWhenSynthesizingLevelId()
    {
        var room = new Element
        {
            Id = 100,
            CategoryId = -2000160,
            References = null,
            Parameters =
            [
                BipHelpers.MakeBipParam(-1001952, "LEVEL_PARAM", StorageType.ElementId, 555),
                BipHelpers.MakeBipParam(-1006922, "ROOM_UPPER_LEVEL", StorageType.ElementId, 777)
            ]
        };
        var info = new RevitFileInfo { Elements = [room] };
        ElementBuilder.ApplyRoomParams(info);

        var levelParam = room.Parameters.FirstOrDefault(p => p.Id == -1006916);
        Assert.NotNull(levelParam);
        Assert.Equal(555, levelParam.Value);

        var upperLevelParam = room.Parameters.FirstOrDefault(p => p.Id == -1006922);
        Assert.NotNull(upperLevelParam);
        Assert.Equal(777, upperLevelParam.Value);
    }
}
