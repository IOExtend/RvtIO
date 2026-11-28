namespace RvtIO.Parsing;

internal static partial class TfBlockDeserializer
{
    private static HashSet<int> ReadViewIdSet(Cursor cursor)
    {
        int count = ReadI32(cursor);
        if (count is < -1 or > 1_000_000 || count * 4L > cursor.End - cursor.Pos) throw Fail;
        var hidden = new HashSet<int>();
        for (int i = 0; i < count; i++) hidden.Add(ReadI32(cursor));
        return hidden;
    }
}
