namespace RvtIO.Parsing;

internal static partial class TfBlockDeserializer
{
    internal sealed record PlanNoteLeader(double[] End, double[] Elbow, double[] Anchor);

    private static (string Text, double? FontSizePoints)? ReadPlanNoteRtf(Cursor cursor,
        IReadOnlyList<TfField> fields)
    {
        if (fields is not [{ Declaring: 3487, Name: "m_arrRtfStream", Type: 2, Mods: 80 }]) throw Fail;
        int count = ReadI32(cursor);
        if (count is -1 or 0) return null;
        int start = cursor.Pos;
        if (count is < 0 or > 1_048_576 || !cursor.Take(count)) throw Fail;
        return RtfText.Parse(cursor.D, start, cursor.Pos);
    }

    private static PlanNoteLeader? ReadPlanNoteLeader(Cursor cursor, IReadOnlyList<TfField> fields,
        List<(ushort, uint)> queue)
    {
        double[]? end = null, elbow = null, anchor = null;
        int type = -1;
        foreach (var field in fields)
        {
            if (field is { Declaring: 622, Name: "m_end", Type: 7, Mods: 16, Cnt: 3 }) end = ReadStoredPoint(cursor);
            else if (field is { Declaring: 622, Name: "m_elbow", Type: 7, Mods: 16, Cnt: 3 }) elbow = ReadStoredPoint(cursor);
            else if (field is { Declaring: 622, Name: "m_anchor", Type: 7, Mods: 16, Cnt: 3 }) anchor = ReadStoredPoint(cursor);
            else if (field is { Declaring: 622, Name: "m_type", Type: 4, Mods: 0 }) type = ReadI32(cursor);
            else ReadField(cursor, field, queue, 1, null);
        }
        return type == 1 && end != null && elbow != null && anchor != null
            && ValidPoint(end) && ValidPoint(elbow) && ValidPoint(anchor)
            ? new(end, elbow, anchor) : null;

        static bool ValidPoint(double[] point) => point.All(value => Math.Abs(value) < 1_000_000);
    }
}
