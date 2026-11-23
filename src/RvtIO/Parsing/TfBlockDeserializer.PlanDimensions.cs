namespace RvtIO.Parsing;

internal static partial class TfBlockDeserializer
{
    private static double[] ReadStoredPoint(Cursor cursor) => [ReadF64(cursor), ReadF64(cursor), ReadF64(cursor)];

    private static (double[] Origin, double[] Direction)? ReadDimensionLine(Cursor cursor,
        IReadOnlyList<TfField> fields, List<(ushort, uint)> queue)
    {
        double[]? origin = null, direction = null;
        foreach (var field in fields)
        {
            if (field is { Declaring: 1736, Name: "m_origin", Type: 7, Mods: 16, Cnt: 3 })
                origin = ReadStoredPoint(cursor);
            else if (field is { Declaring: 1736, Name: "m_dirVec", Type: 7, Mods: 16, Cnt: 3 })
                direction = ReadStoredPoint(cursor);
            else
                ReadField(cursor, field, queue, 1, null);
        }
        return origin != null && direction != null ? (origin, direction) : null;
    }
}
