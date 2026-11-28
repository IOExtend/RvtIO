namespace RvtIO.Parsing;

internal static partial class TfBlockDeserializer
{
    internal sealed record SiteContour(double Elevation, bool Primary, List<List<(double X, double Y)>> Lines);
    internal sealed record SiteContourLabelData(int SurfaceId, double[][] Positions, double[][] Directions);

    private static SiteContourLabelData? ReadSiteContourLabels(Cursor cursor, List<TfField> fields,
        List<(ushort, uint)> queue)
    {
        if (fields is not
            [{ Name: "m_oAssocPropData" }, { Name: "m_textPos", Type: 13, Mods: 80 },
                { Name: "m_textDir", Type: 13, Mods: 80 }, { Name: "m_textDirPerp", Type: 13, Mods: 80 },
                { Name: "m_surfId", Type: 14, Mods: 0, Ref: 20 }, { Name: "m_curveStatus", Type: 4, Mods: 0 },
                { Name: "m_makePrimaryOnly", Type: 1, Mods: 0 }]) throw Fail;

        ReadField(cursor, fields[0], queue, 1, null);
        var positions = Points();
        var directions = Points();
        var perpendiculars = Points();
        int surface = ReadI32(cursor), status = ReadI32(cursor);
        if (!cursor.Take(1)) throw Fail;
        return status == 0 && surface > 0 && positions.Length == directions.Length
            && positions.Length == perpendiculars.Length ? new(surface, positions, directions) : null;

        double[][] Points()
        {
            uint count = ReadU32(cursor);
            if (count == uint.MaxValue) return [];
            if (count > 100_000 || count > (cursor.End - cursor.Pos) / 24) throw Fail;
            var points = new double[count][];
            for (int i = 0; i < points.Length; i++)
            {
                points[i] = [ReadF64(cursor), ReadF64(cursor), ReadF64(cursor)];
                if (points[i].Any(value => !(Math.Abs(value) < 1_000_000))) throw Fail;
            }
            return points;
        }
    }

    private static List<SiteContour> ReadSiteContours(Cursor cursor)
    {
        // ContourManager owns an inline map of elevations to polyline wrappers and facet indices.
        if (SerializationFields(845) is not
            [{ Name: "first", Type: 7, Mods: 0 }, { Name: "second", Type: 14, Mods: 0 }]
            || SerializationFields(846) is not
            [{ Name: "m_arr", Type: 14, Mods: 80 }, { Name: "m_facets", Type: 4, Mods: 80 },
                { Name: "m_isPrimary", Type: 1, Mods: 0 }]
            || SerializationFields(847) is not [{ Name: "m_arr", Type: 13, Mods: 80 }])
            throw Fail;
        uint count = Count(cursor, 10_000);
        var result = new List<SiteContour>((int)count);
        for (uint i = 0; i < count; i++)
        {
            double level = ReadF64(cursor);
            if (!double.IsFinite(level)) throw Fail;
            uint lines = Count(cursor, 100_000);
            var curves = new List<List<(double X, double Y)>>((int)lines);
            for (uint j = 0; j < lines; j++)
            {
                uint points = Count(cursor, 100_000);
                if (points > (cursor.End - cursor.Pos) / 24) throw Fail;
                var vertices = new List<(double X, double Y)>((int)points);
                for (uint k = 0; k < points; k++)
                {
                    double x = ReadF64(cursor), y = ReadF64(cursor), z = ReadF64(cursor);
                    if (!(Math.Abs(x) < 1_000_000 && Math.Abs(y) < 1_000_000 && Math.Abs(z - level) < 1e-6))
                        throw Fail;
                    vertices.Add((x, y));
                }
                if (vertices.Count > 1) curves.Add(vertices);
            }
            uint facets = Count(cursor, 1_000_000);
            if (!cursor.Take((int)facets * 4) || cursor.Pos == cursor.End) throw Fail;
            byte primary = cursor.D[cursor.Pos++];
            if (primary > 1) throw Fail;
            result.Add(new(level, primary == 1, curves));
        }
        return result;

        static uint Count(Cursor cursor, uint limit)
        {
            uint count = ReadU32(cursor);
            if (count == uint.MaxValue) return 0;
            return count <= limit ? count : throw Fail;
        }
    }
}
