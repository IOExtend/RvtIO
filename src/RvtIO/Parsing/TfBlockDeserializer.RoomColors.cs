namespace RvtIO.Parsing;

internal static partial class TfBlockDeserializer
{
    internal sealed record RoomColorEntry(string Name, uint Color, int PatternId);

    private static List<RoomColorEntry> ReadRoomColorEntries(Cursor cursor)
    {
        uint count = ReadU32(cursor);
        var colors = new List<RoomColorEntry>();
        if (count == uint.MaxValue) return colors;
        if (count > 10_000) throw Fail;
        for (uint i = 0; i < count; i++)
        {
            ReadString(cursor); // Caption is independent of the value used to select a room.
            string name = ReadString(cursor);
            ReadF64(cursor);
            ReadI32(cursor); // ElementId and integer storage slots.
            ReadI32(cursor);
            int storage = ReadI32(cursor);
            uint color = ReadU32(cursor);
            int pattern = ReadI32(cursor);
            if (!cursor.Take(6)) throw Fail;
            var flags = cursor.D.AsSpan(cursor.Pos - 6, 6);
            if (storage == 3 && name.Length > 0 && color <= 0xFFFFFF
                && flags[0] == 0 && flags[3] == 0 && flags[5] == 0)
                colors.Add(new RoomColorEntry(name, color, pattern));
        }
        return colors;
    }
}
