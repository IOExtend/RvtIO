namespace RvtIO.Parsing;

internal static partial class TfBlockDeserializer
{
    internal sealed record FamilyAnnotationArc(double X, double Y, double Radius, double Start, double End, int StyleId);

    internal static (TitleBlockLine? Line, FamilyAnnotationArc? Arc, bool TypeDependent, int SketchId)?
        ReadFamilyAnnotationCurve(byte[] data, int start, uint size)
    {
        var fields = SerializationFields(838);
        if (fields == null || size < 6 || start < 0 || (long)start + size + 16 > data.Length
            || BitConverter.ToUInt16(data, start + 12) != 838
            || BitConverter.ToUInt32(data, start + 12 + (int)size) != size) return null;
        var queue = new List<(ushort Cls, uint Id)>();
        var cursor = new Cursor
        {
            D = data, Pos = start + 14, End = start + 12 + (int)size, RootClassId = 838, TailWalk = true, RootQueue = queue
        };
        int driver = -1, curve = -1, sketch = -1;
        try
        {
            foreach (var field in fields)
            {
                int next = queue.Count;
                ReadField(cursor, field, queue, 0, null);
                if (field is { Declaring: 838, Name: "m_pCurveDriver", Type: 14, Mods: 2 }
                    && queue.Count == next + 1 && queue[next].Cls == 929)
                    driver = next;
            }
            if (driver < 0) return null;
            for (int i = 0; i < queue.Count; i++)
            {
                if (i == curve)
                {
                    bool typeDependent = queue.Any(entry => entry.Cls == 1806);
                    if (queue[i].Cls == 1974)
                        return ReadFamilyAnnotationArc(cursor) is { } arc ? (null, arc, typeDependent, sketch) : null;
                    if (!TryReadGLine(cursor, queue, i, out var line) || (line.Flags & 0x20) != 0) return null;
                    double x0 = line.Ox + line.T0 * line.Dx, y0 = line.Oy + line.T0 * line.Dy;
                    double x1 = line.Ox + line.T1 * line.Dx, y1 = line.Oy + line.T1 * line.Dy;
                    if (x0 == x1 && y0 == y1
                        || !(Math.Abs(line.Oz + line.T0 * line.Dz) < 1e-8
                            && Math.Abs(line.Oz + line.T1 * line.Dz) < 1e-8)) return null;
                    return Math.Abs(x0) < 1 && Math.Abs(x1) < 1 && Math.Abs(y0) < 1 && Math.Abs(y1) < 1
                        ? (new(x0, y0, x1, y1, line.CategoryId), null, typeDependent, sketch) : null;
                }
                cursor.TailClassId = queue[i].Cls;
                foreach (var field in SerializationFields(queue[i].Cls) ?? throw Fail)
                {
                    int next = queue.Count;
                    if (queue[i].Cls == 3606 && field is { Declaring: 422, Name: "m_groupId", Type: 14, Mods: 0, Ref: 20 })
                        sketch = ReadI32(cursor);
                    else ReadField(cursor, field, queue, 1, null);
                    if (i == driver && field is { Declaring: 87, Name: "m_pCrv" }
                        && queue.Count == next + 1 && queue[next].Cls is 1736 or 1974)
                        curve = next;
                }
            }
        }
        catch (DecodeFail) { return null; }
        return null;
    }

    private static FamilyAnnotationArc? ReadFamilyAnnotationArc(Cursor cursor)
    {
        if (SerializationFields(1974) is not { Count: 7 } fields
            || fields[0] is not { Declaring: 1246, Name: "m_GInfo", Type: 14, Mods: 0 }
            || fields[1] is not { Declaring: 1737, Name: "m_endParams", Type: 7, Mods: 16, Cnt: 2 }
            || fields[2] is not { Declaring: 1974, Name: "m_xVec", Type: 7, Mods: 16, Cnt: 3 }
            || fields[3] is not { Declaring: 1974, Name: "m_yVec", Type: 7, Mods: 16, Cnt: 3 }
            || fields[4] is not { Declaring: 1974, Name: "m_radius", Type: 7, Mods: 0 }
            || fields[5] is not { Declaring: 1974, Name: "m_center", Type: 7, Mods: 16, Cnt: 3 }
            || fields[6] is not { Declaring: 1974, Name: "m_bFilled", Type: 1, Mods: 0 }
            || !cursor.Take(8)) return null;
        int styleId = ReadI32(cursor);
        uint flags = ReadU32(cursor);
        double start = ReadF64(cursor), end = ReadF64(cursor);
        double xx = ReadF64(cursor), xy = ReadF64(cursor), xz = ReadF64(cursor);
        double yx = ReadF64(cursor), yy = ReadF64(cursor), yz = ReadF64(cursor);
        double radius = ReadF64(cursor);
        double x = ReadF64(cursor), y = ReadF64(cursor), z = ReadF64(cursor);
        if (!cursor.Take(1) || cursor.D[cursor.Pos - 1] != 0 || (flags & 0x20) != 0
            || !(radius is > 0 and < 1) || !(Math.Abs(x) < 1 && Math.Abs(y) < 1)
            || z != 0 || xz != 0 || yz != 0
            || !(Math.Abs(xx * xx + xy * xy - 1) < 1e-8 && Math.Abs(yx * yx + yy * yy - 1) < 1e-8
                && Math.Abs(xx * yx + xy * yy) < 1e-8)
            || !double.IsFinite(start) || !double.IsFinite(end) || !(end - start is >= 0 and <= 2 * Math.PI + 1e-8))
            return null;
        if (start == end) end = start + 2 * Math.PI;
        double angle = Math.Atan2(xy, xx), sign = Math.Sign(xx * yy - xy * yx);
        return new(x, y, radius, (angle + sign * start) * (180 / Math.PI),
            (angle + sign * end) * (180 / Math.PI), styleId);
    }
}
