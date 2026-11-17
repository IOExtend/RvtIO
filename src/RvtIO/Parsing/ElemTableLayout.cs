namespace RvtIO.Parsing;

/// <summary>
/// Global/ElemTable of any Revit version, laid out by the file's own schema (<see cref="FormatsSchema"/>) instead of a
/// per-version constant. The decoded stream is <c>[u16 ElemTable class id][u32 m_elemArr count][count ElemRec rows][rest]</c>;
/// an ElemRec row is its fields in declared order, each field its class's fields in declared order, down to the leaf
/// integers: type 0x04 is 4 bytes, type 0x0B is 8 bytes. The element id is <c>ElementId.m_id</c> -> <c>Identifier</c>, which is a
/// 4-byte <c>m_id</c> up to Revit 2023 (28-byte rows) and an 8-byte <c>m_id64</c> from Revit 2024 (40-byte rows, and
/// <c>m_history</c> is declared first). The class id equals the schema's ElemTable id, the rows are in strictly increasing id
/// order, and every row of the main partition's leading record run is one of these ids.
/// </summary>
internal static class ElemTableLayout
{
    internal readonly record struct Row(long Id, long OriginalId, int CreationDate, int PartitionId, long OwningId, int? LastModificationDate = null);

    internal const int HeaderLength = 6;

    /// <summary>Byte width of one ElemRec row of this schema, or null when the schema does not have the expected shape.</summary>
    internal static int? RowLength(FormatsSchema schema)
        => schema.Get("ElemRec") is { } rec && Width(schema, rec, 0) is int width && Agrees(SchemaRelease.RowLength(schema.Release), width) ? width : null;

    /// <summary>Byte width of the element id field (<c>Identifier</c>): 4 or 8, null when it is neither.</summary>
    internal static int? IdWidth(FormatsSchema schema)
        => schema.Get("Identifier") is { Fields: [{ } field] } && LeafWidth(field.Type) is int width && Agrees(SchemaRelease.IdWidth(schema.Release), width) ? width : null;

    /// <summary>True when the schema declares an element id width that the release in force does not have (a file saved by an older
    /// release than its schema): the layout of that release is not the layout of the bytes.</summary>
    internal static bool DisagreesWithRelease(FormatsSchema schema)
        => schema.Get("Identifier") is { Fields: [{ } field] } && LeafWidth(field.Type) is int width && !Agrees(SchemaRelease.IdWidth(schema.Release), width);

    /// <summary>The widths the schema declares must be those of the release in force (the older of the saved and the schema release);
    /// a file whose two releases disagree on the layout is not decoded by a layout it does not have.</summary>
    private static bool Agrees(int? releaseWidth, int declared) => releaseWidth is not int expected || expected == declared;

    /// <summary>The rows of a decoded Global/ElemTable stream, or null unless the stream is this schema's ElemTable: the class
    /// id and the m_elemArr element class match, every row fits, and the ids strictly increase.</summary>
    internal static List<Row>? TryParse(byte[] data, FormatsSchema schema)
    {
        if (schema.Get("ElemTable") is not { Fields: [{ Name: "m_elemArr" } array, ..] } table
            || schema.Get("ElemRec") is not { } rec
            || array.Ref != rec.Id
            || data.Length < HeaderLength
            || BitConverter.ToUInt16(data, 0) != table.Id
            || RowLength(schema) is not int rowLength)
            return null;
        uint count = BitConverter.ToUInt32(data, 2);
        if (HeaderLength + (long)count * rowLength > data.Length) return null;

        // offsets of the leaves the reader needs, found by their declared path
        int? id = Offset(schema, rec, ["m_id"]), original = Offset(schema, rec, ["m_history", "m_originalElementId"]),
            created = Offset(schema, rec, ["m_history", "m_creationDate"]), modified = Offset(schema, rec, ["m_history", "m_lastModificationDate"]), partition = Offset(schema, rec, ["m_partitionId"]),
            owner = Offset(schema, rec, ["m_OwningElementId"]);
        if (id is null || original is null || created is null || partition is null || owner is null) return null;
        int idWidth = IdWidth(schema)!.Value;
        if (schema.Get("OriginalElementId") is not { } originalClass || Width(schema, originalClass, 0) != idWidth) return null;

        var rows = new List<Row>((int)count);
        long previous = -1;
        for (int i = 0; i < count; i++)
        {
            int at = HeaderLength + i * rowLength;
            long rowId = Read(data, at + id.Value, idWidth);
            if (rowId <= previous) return null;
            previous = rowId;
            rows.Add(new Row(rowId, Read(data, at + original.Value, idWidth), BitConverter.ToInt32(data, at + created.Value),
                BitConverter.ToInt32(data, at + partition.Value), Read(data, at + owner.Value, idWidth), modified is int m ? BitConverter.ToInt32(data, at + m) : null));
        }
        return rows;
    }

    private static long Read(byte[] data, int at, int width)
        => width == 8 ? BitConverter.ToInt64(data, at) : BitConverter.ToInt32(data, at);

    private static int? LeafWidth(byte type) => type switch { 0x04 => 4, 0x0B => 8, _ => null };

    private static int? Width(FormatsSchema schema, FormatsSchema.ClassDef def, int depth)
    {
        if (depth > 8) return null;
        int total = 0;
        foreach (var field in def.Fields)
        {
            int? w = FieldWidth(schema, field, depth);
            if (w is null) return null;
            total += w.Value;
        }
        return total;
    }

    /// <summary>Byte offset inside a row of the leaf reached by the field names in <paramref name="path"/>, followed down through
    /// single-field wrapper classes (ElementId -> Identifier) until a leaf; null when a name is missing. Internal for the
    /// downgrade ElemTable transcoder, which reuses the same offsets to copy leaves between layouts.</summary>
    internal static int? Offset(FormatsSchema schema, FormatsSchema.ClassDef def, string[] path)
    {
        int offset = 0;
        for (int step = 0; step < path.Length; step++)
        {
            int? found = null;
            foreach (var field in def.Fields)
            {
                if (field.Name == path[step]) { found = offset; break; }
                int? w = FieldWidth(schema, field);
                if (w is null) return null;
                offset += w.Value;
            }
            if (found is null) return null;
            var current = def.Fields.First(f => f.Name == path[step]);
            if (step + 1 < path.Length)
            {
                if (current.Ref is not int r || !schema.Classes.TryGetValue(r, out var next)) return null;
                def = next;
            }
        }
        return offset;
    }

    /// <summary>A class field is its class's fields, a leaf is its integer width. A pointer member (type 0x0E with a pointer
    /// flavour in the low nibble of the mods and no class: Revit 2027 declares m_pElement, m_pGRep and m_pHeader in ElemRec) is
    /// in memory only and takes no bytes: the rows stay 40 bytes.</summary>
    private static int? FieldWidth(FormatsSchema schema, FormatsSchema.Field field, int depth = 0)
    {
        if (field.Type == 0x0E && (field.Mods & 0x0F) != 0 && field.Ref is null) return 0;
        return field.Type == 0x0E && field.Mods == 0 && field.Ref is int r && schema.Classes.TryGetValue(r, out var inner)
            ? Width(schema, inner, depth + 1) : LeafWidth(field.Type);
    }
}
