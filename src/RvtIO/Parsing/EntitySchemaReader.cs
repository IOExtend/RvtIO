using System.Buffers.Binary;
using OpenMcdf;

namespace RvtIO.Parsing;

/// <summary>
/// The extensible storage schemas of a file. Global/Latest is an ADocument that the class-schema walk reads; its ESSchemaStorage
/// holds the schemas the entities of the document use: in m_storedSchemas (pairs of GUID and ESSchema) in the class schemas up to
/// Revit 2024, in m_schemaUsageMap (pairs of GUID and SchemaUsageInfo { m_contentDocsKeys, m_schema, m_usedInHost }) from Revit 2025.
/// A schema is taken by its own ESSchema.m_guid, so both holders are read alike. An entity stores the values of the fields of its
/// schema (ESField: name, type name, container, entry index) and nothing else, so the walk of a record (<see cref="WideBlockWalker"/>)
/// reads the values of an entity by the fields listed here.
/// </summary>
internal static class EntitySchemaReader
{
    /// <summary>Per schema GUID the fields of an entity as fields of the class schema, in the order an entity stores them; null for a
    /// schema with a field whose type is not read or whose entry indexes are not 0 to n-1. Empty when Global/Latest is missing, does not decode or is not walked without an
    /// error: no schema is known then, and a walk that is given the empty set stops at every entity that has values.</summary>
    internal static Dictionary<Guid, SchemaField[]?> Read(RootStorage root, SchemaDocument schema, Dictionary<Guid, string>? publicNames = null) =>
        PagedGzipStream.Read(root, "Global", "Latest") is { } latest && Read(latest, schema, publicNames) is { } schemas ? schemas : [];

    /// <summary>The entity schemas of each loaded family document, by the key of its content block (<see cref="PartitionFraming"/>, the
    /// same text): an entity in a family block names a schema of its own document, which Global/Latest need not hold. Global/ContentDocuments
    /// holds per content document a ContentMarker object laid out as a content block of a partition stream (handle 0xFFFFFFFF, the
    /// ContentKey tag, a u32 count, which is 0xFFFFFFFF, then m_guidKey, 16 bytes), u32 length, that many bytes of the document's own
    /// ADocument, and the length again; a ContentMarker with the handle 0, its count and a length of 0 ends it at its last byte. Each
    /// ADocument is read like Global/Latest; a document whose ADocument is not walked without an error has no schemas. Empty when the
    /// stream is missing, does not decode or does not end so.</summary>
    internal static Dictionary<string, Dictionary<Guid, SchemaField[]?>> ReadContentDocuments(RootStorage root, SchemaDocument schema,
        IReadOnlySet<string>? wantedKeys = null, Dictionary<string, Dictionary<Guid, string>>? publicNames = null)
    {
        var documents = new Dictionary<string, Dictionary<Guid, SchemaField[]?>>(StringComparer.Ordinal);
        if (PagedGzipStream.Read(root, "Global", "ContentDocuments") is not { } data
            || schema.Classes.FirstOrDefault(c => c.Name == "ContentMarker") is not { } marker
            || schema.Classes.FirstOrDefault(c => c.Name == "ContentKey") is not { } key) return documents;
        var span = data.AsSpan();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int at = 0; ;)
        {
            if (at + 6 > span.Length || BinaryPrimitives.ReadUInt16LittleEndian(span[at..]) != marker.Id) return [];
            uint handle = BinaryPrimitives.ReadUInt32LittleEndian(span[(at + 2)..]);
            if (handle == 0)
                return at + 14 == span.Length && BinaryPrimitives.ReadUInt32LittleEndian(span[(at + 10)..]) == 0 ? documents : [];
            if (handle != uint.MaxValue || at + 32 > span.Length || BinaryPrimitives.ReadUInt16LittleEndian(span[(at + 6)..]) != key.Id) return [];
            string name = Convert.ToHexString(span.Slice(at + 12, 16));
            int length = BinaryPrimitives.ReadInt32LittleEndian(span[(at + 28)..]);
            if (length < 0 || length > span.Length - at - 36 || BinaryPrimitives.ReadInt32LittleEndian(span[(at + 32 + length)..]) != length) return [];
            if (!seen.Add(name)) return [];
            if (wantedKeys == null || wantedKeys.Contains(name))
            {
                var names = publicNames == null ? null : new Dictionary<Guid, string>();
                documents.Add(name, Read(span.Slice(at + 32, length).ToArray(), schema, names) ?? []);
                if (names is { Count: > 0 }) publicNames!.Add(name, names);
            }
            at += 36 + length;
        }
    }

    /// <summary>The same of a decoded Global/Latest; null when it is not an ADocument or its walk does not end without an error.</summary>
    internal static Dictionary<Guid, SchemaField[]?>? Read(byte[] latest, SchemaDocument schema, Dictionary<Guid, string>? publicNames = null)
    {
        if (latest.Length < 2) return null;
        // the step budget of the record walks of the census: a walk that is out of step can meet a large count of items without bytes
        var walker = new WideBlockWalker(schema, (_, _) => null, ["ESSchema", "ESField"]) { StepBudget = 16L * latest.Length + 65536 };
        int root = BitConverter.ToUInt16(latest, 0);
        if (walker.Class(root)?.Name != "ADocument") return null;
        // no entity schema is known yet: an entity with values in the document itself stops this walk, it is not passed
        walker.UseEntitySchemas(new Dictionary<Guid, SchemaField[]?>());
        var walk = walker.Run(latest, 0, latest.Length, root);
        if (walk.Error != null || !walk.RootRead) return null;

        var classIds = schema.Classes.GroupBy(c => c.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Id, StringComparer.Ordinal);
        var schemas = new Dictionary<Guid, SchemaField[]?>();
        var fields = new List<(long Index, SchemaField? Field)>();
        // the records come in stored order: the fields of a schema, then the schema
        foreach (var (classId, _, values) in walk.Records)
        {
            if (walker.Class(classId)!.Name == "ESField")
            {
                fields.Add((values.GetValueOrDefault("m_entryIndex") is long index ? index : -1, Field(values, classIds)));
                continue;
            }
            // an entity stores its fields in the order of ESField.m_entryIndex, which is not the order the schema lists them in
            if (values.GetValueOrDefault("m_guid") is Guid guid)
            {
                SchemaField[]? layout = fields.Any(f => f.Field == null) || !Numbered(fields.Select(f => f.Index)) ? null
                    : [.. fields.OrderBy(f => f.Index).Select(f => f.Field!)];
                if (!schemas.TryAdd(guid, layout)) publicNames?.Remove(guid);
                else if (layout != null && values.GetValueOrDefault("m_readAccessLevel") is 1L
                    && values.GetValueOrDefault("m_schemaName") is string name)
                    publicNames?.Add(guid, name);
            }
            fields.Clear();
        }
        return schemas;
    }

    // the entry indexes of a schema number its fields from 0 without a gap or a repeat; any other set gives no order to read by
    private static bool Numbered(IEnumerable<long> indexes) => indexes.Order().Select((index, position) => index == position).All(same => same);

    /// <summary>The class-schema field that is stored like an entity field. m_containerType 0 is a single value, 1 an array (u32 count
    /// and the values), 2 a map (u32 count and the pairs; the pair class is the type). m_fieldTypeName is TCHAR (a text), a scalar
    /// (bool and char 1 byte, short 2, int and float 4, double and int64_t 8) or the name of a class of the class schema, read inline
    /// (ElementId, XYZ, UV, GUIDvalue, AStringWrapper, the std::pair classes, and ESEntity: a nested entity, which queues its own values).
    /// Records of the 52 measured files that are walked to their last byte confirm: a text; int, bool, float and double; arrays of int,
    /// char and double; ElementId (4 and 8 bytes), XYZ and nested ESEntity, each single and as an array; a single GUIDvalue; arrays of
    /// AStringWrapper; maps from int to ElementId, from ElementId to ESEntity and from AString to AString and to ESEntity. No record
    /// confirms short, int64_t, a single char, arrays of bool, float or GUIDvalue, or the maps whose pairs occur only empty (from AString
    /// to ElementId, GUIDvalue, UV, XYZ, bool, char, double and int): they are read by the same grammar, because the class schema
    /// itself gives their widths (the fields of its std::pair classes). An array of texts occurs in no schema and is not read.</summary>
    private static SchemaField? Field(Dictionary<string, object?> values, Dictionary<string, int> classIds)
    {
        if (values.GetValueOrDefault("m_fieldName") is not string name || values.GetValueOrDefault("m_fieldTypeName") is not string type
            || values.GetValueOrDefault("m_containerType") is not long container || container is < 0 or > 2) return null;
        byte mods = container == 0 ? (byte)0 : (byte)0x50;
        if (type == "TCHAR") return container == 0 ? new SchemaField(name, 8, 0x60, 0, null, null, null, null) : null;
        byte scalar = type switch { "bool" => 1, "char" => 2, "short" => 3, "int" => 4, "float" => 6, "double" => 7, "int64_t" => 11, _ => 0 };
        if (scalar != 0) return new SchemaField(name, scalar, mods, 0, null, null, null, null);
        return classIds.TryGetValue(type, out int id) ? new SchemaField(name, 14, mods, 0, null, id, null, null) : null;
    }
}
