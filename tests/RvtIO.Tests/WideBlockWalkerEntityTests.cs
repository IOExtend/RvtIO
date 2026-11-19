using System.Text;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

/// <summary>The values of an extensible storage entity in the field walk (<see cref="WideBlockWalker"/>): ESEntity.m_blob names the
/// schema of the entity by its GUID where a class tag would stand, the values are queued like an owned object, and they are read by
/// the fields of that schema, which the document lists (<see cref="EntitySchemaReader"/>). Hand-made blocks, no private file.</summary>
public class WideBlockWalkerEntityTests
{
    private const int IdentifierId = 1, ElementIdId = 2, GuidId = 3, BlobId = 4, EntityId = 5, EntityPairId = 6, CellId = 7, DataId = 8, ModelId = 9,
        HostId = 10, XyzId = 11, TextIntId = 12, FieldId = 13, SchemaId = 14, SchemaPairId = 15, UsageId = 16, UsagePairId = 17, StorageId = 18,
        DocumentId = 19;

    private static readonly Guid SchemaA = new("11111111-2222-3333-4444-555555555555"), SchemaB = new("66666666-7777-8888-9999-aaaaaaaaaaaa");

    private static SchemaField Field(string name, byte type, byte mods = 0, int? reference = null, int? fixedCount = null) =>
        new(name, type, mods, 0, fixedCount, reference, null, null);

    /// <summary>The classes of an element with an entity (Host: the cell of its entities, then a Model that owns a Data) and of the
    /// document that lists the entity schemas. <paramref name="wideIds"/>: an element id of 8 bytes (from Revit 2024) or of 4;
    /// <paramref name="usageMap"/>: the storage holds its schemas in m_schemaUsageMap (from Revit 2025) or in m_storedSchemas.</summary>
    private static SchemaDocument Schema(bool wideIds = true, bool usageMap = false) => new()
    {
        Classes =
        [
            new(IdentifierId, "Identifier", 0, 1, [wideIds ? Field("m_id64", 11) : Field("m_id", 4)], []),
            new(ElementIdId, "ElementId", 0, 1, [Field("m_id", 14, 0, IdentifierId)], []),
            new(GuidId, "GUIDvalue", 0, 1, [Field("m_guid", 9)], []),
            new(BlobId, "ESBlob", 0, 1, [], []),
            new(EntityId, "ESEntity", 0, 1, [Field("m_blob", 14, 1)], []),
            new(EntityPairId, "std::pair< GUIDvalue, ESEntity >", 0, 1, [Field("first", 14, 0, GuidId), Field("second", 14, 0, EntityId)], []),
            new(CellId, "ESEntityCell", 0, 1, [Field("m_entityMap", 14, 0x50, EntityPairId)], []),
            new(DataId, "Data", 0, 1, [Field("m_name", 8, 0x60), Field("m_value", 8, 0x60)], []),
            new(ModelId, "Model", 0, 1, [Field("m_oData", 14, 1)], []),
            new(HostId, "Host", 0, 1, [Field("m_pEntities", 14, 1), Field("m_pModel", 14, 1)], []),
            new(XyzId, "XYZ", 0, 1, [Field("m_xyz", 7, 0x10, null, 3)], []),
            new(TextIntId, "std::pair< AString, int >", 0, 1, [Field("first", 8, 0x60), Field("second", 4)], []),
            new(FieldId, "ESField", 0, 1,
                [Field("m_fieldName", 8, 0x60), Field("m_fieldTypeName", 8, 0x60), Field("m_containerType", 4), Field("m_entryIndex", 4)], []),
            new(SchemaId, "ESSchema", 0, 1, [Field("m_fields", 14, 0x50, FieldId), Field("m_schemaName", 8, 0x60), Field("m_guid", 14, 0, GuidId)], []),
            new(SchemaPairId, "std::pair< GUIDvalue, ESSchema >", 0, 1, [Field("first", 14, 0, GuidId), Field("second", 14, 0, SchemaId)], []),
            new(UsageId, "SchemaUsageInfo", 0, 1,
                [Field("m_contentDocsKeys", 14, 0x50, GuidId), Field("m_schema", 14, 0, SchemaId), Field("m_usedInHost", 1)], []),
            new(UsagePairId, "std::pair< GUIDvalue, SchemaUsageInfo >", 0, 1, [Field("first", 14, 0, GuidId), Field("second", 14, 0, UsageId)], []),
            new(StorageId, "ESSchemaStorage", 0, 1,
                [usageMap ? Field("m_schemaUsageMap", 14, 0x50, UsagePairId) : Field("m_storedSchemas", 14, 0x50, SchemaPairId)], []),
            new(DocumentId, "ADocument", 0, 1, [Field("m_pStorage", 14, 1)], []),
        ],
    };

    private static byte[] Block(ushort classId, Action<BinaryWriter> body)
    {
        var stream = new MemoryStream();
        var w = new BinaryWriter(stream, Encoding.Unicode);
        w.Write(classId);
        body(w);
        return stream.ToArray();
    }

    private static void Text(BinaryWriter w, string s) { w.Write((uint)s.Length); w.Write(Encoding.Unicode.GetBytes(s)); }

    // listed by name, as the storage lists them; an entity stores its values by entry index: Name, Count, Values, Owner, Map, Inner
    private static readonly (string Name, string Type, int Container, int Index)[] FieldsA =
    [
        ("Count", "int", 0, 1), ("Inner", "ESEntity", 0, 5), ("Map", "std::pair< AString, int >", 2, 4), ("Name", "TCHAR", 0, 0),
        ("Owner", "ElementId", 0, 3), ("Values", "double", 1, 2),
    ];

    private static readonly (string Name, string Type, int Container, int Index)[] FieldsB = [("Flag", "bool", 0, 0), ("Origin", "XYZ", 0, 1)];

    /// <summary>The decoded Global/Latest of a document that stores the given entity schemas, in either holder of the storage.</summary>
    private static byte[] Latest(bool usageMap, params (Guid Guid, (string Name, string Type, int Container, int Index)[] Fields)[] schemas) =>
        Block(DocumentId, w =>
        {
            w.Write(0xFFFFFFFFu); w.Write((ushort)StorageId);               // m_pStorage
            w.Write((uint)schemas.Length);
            foreach (var (guid, fields) in schemas)
            {
                w.Write(guid.ToByteArray());                                // the key of the pair
                if (usageMap) { w.Write(2u); w.Write(new byte[16]); w.Write(SchemaB.ToByteArray()); }   // m_contentDocsKeys: two other GUIDs
                w.Write((uint)fields.Length);                               // m_fields
                foreach (var (name, type, container, index) in fields) { Text(w, name); Text(w, type); w.Write(container); w.Write(index); }
                Text(w, "Name");                                            // m_schemaName
                w.Write(guid.ToByteArray());                                // m_guid
                if (usageMap) w.Write((byte)1);                             // m_usedInHost
            }
            w.Write(0u);                                                    // the four bytes behind the last object
        });

    /// <summary>A Host whose cell holds one entity of schema A. The values of the entity are queued by the cell: they lie behind the
    /// Model, which the Host queued before, and in front of the Data, which the Model queues after.</summary>
    private static byte[] HostBlock(Action<BinaryWriter>? values, uint handle = 0xFFFFFFFF, Action<BinaryWriter>? behind = null) => Block(HostId, w =>
    {
        w.Write(0xFFFFFFFFu); w.Write((ushort)CellId);                      // m_pEntities
        w.Write(0xFFFFFFFFu); w.Write((ushort)ModelId);                     // m_pModel
        w.Write(1u); w.Write(SchemaA.ToByteArray());                        // the cell: one entity, its key
        w.Write(handle);                                                    // m_blob: the handle
        if (handle != 0) w.Write(SchemaA.ToByteArray());                    // and the GUID of the schema where a class tag would stand
        w.Write(0xFFFFFFFFu); w.Write((ushort)DataId);                      // the model: m_oData
        values?.Invoke(w);                                                  // the values of the entity
        Text(w, "FieldName"); Text(w, "Stored");                            // the data
        behind?.Invoke(w);                                                  // what the values of the entity queued
    });

    private static void TextAndInt(BinaryWriter w) { Text(w, "Stored"); w.Write(5); }

    private static readonly (string Name, string Type, int Container, int Index)[] TextAndIntFields = [("Count", "int", 0, 1), ("Name", "TCHAR", 0, 0)];

    private static WideBlockWalker Walker(SchemaDocument schema, IReadOnlyDictionary<Guid, SchemaField[]?>? entitySchemas)
    {
        var walker = new WideBlockWalker(schema, (declaring, field) => declaring.Name + "." + field.Name, []);
        if (entitySchemas != null) walker.UseEntitySchemas(entitySchemas);
        return walker;
    }

    private static WideBlockWalker Walker(params (Guid Guid, (string Name, string Type, int Container, int Index)[] Fields)[] schemas) =>
        Walker(Schema(), EntitySchemaReader.Read(Latest(false, schemas), Schema())!);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Walk_ReadsTheValuesOfAnEntityByItsSchema_WhereTheQueueReachesThem(bool wideIds)
    {
        var schema = Schema(wideIds);
        var walker = Walker(schema, EntitySchemaReader.Read(Latest(false, (SchemaA, FieldsA), (SchemaB, FieldsB)), schema)!);
        var block = HostBlock(w =>
        {
            TextAndInt(w);                                                  // Name, Count
            w.Write(2u); w.Write(1.5); w.Write(2.5);                        // Values: a count and the values
            if (wideIds) w.Write(4711L); else w.Write(4711);                // Owner: an element id of the width of the file
            w.Write(1u); Text(w, "k"); w.Write(7);                          // Map: a count and the pairs
            w.Write(0xFFFFFFFFu); w.Write(SchemaB.ToByteArray());           // Inner: an entity, whose own values are queued in turn
        }, behind: w => { w.Write((byte)1); w.Write(1.0); w.Write(2.0); w.Write(3.0); });

        var walk = walker.Run(block, 0, block.Length, HostId);

        Assert.True(walk.Exact, walk.Error);
        Assert.Equal(["ESEntityCell", "Model", "ESBlob", "Data", "ESBlob"], walk.Queue.Select(entry => walker.Class(entry)!.Name));
        var captures = walk.Captures.ToLookup(c => c.Key);
        Assert.Equal(("Stored", 2), (captures["ESBlob.Name"].Single().Text, captures["ESBlob.Name"].Single().QueueIndex));
        Assert.Equal(5, captures["ESBlob.Count"].Single().Integer);
        Assert.Equal((2, 1.5), (captures["ESBlob.Values#"].Single().Integer, captures["ESBlob.Values@0"].Single().Real));
        Assert.Equal(4711, captures["ESBlob.Owner"].Single().Integer);
        Assert.Equal(("k", 7), (captures["std::pair< AString, int >.first"].Single().Text, captures["std::pair< AString, int >.second"].Single().Integer));
        Assert.Equal(("FieldName", "Stored"), (captures["Data.m_name"].Single().Text, captures["Data.m_value"].Single().Text));
        Assert.Equal((1, 4), (captures["ESBlob.Flag"].Single().Integer, captures["ESBlob.Flag"].Single().QueueIndex));
        Assert.Equal(3.0, captures["ESBlob.Origin@2"].Single().Real);
        // the readers keep out of their output what lies behind an entity, so the first object that held one is marked as without the schemas
        Assert.Equal(0, walk.EmptyEntityAt);
    }

    [Fact]
    public void ProductionFieldReaderPreservesANameQueuedAfterANestedEntity()
    {
        var schema = Schema(usageMap: true);
        schema.Classes[schema.Classes.FindIndex(c => c.Id == DataId)] = new(DataId, "SymbolInfo", 0, 1,
            [Field("m_name", 8, 0x60), Field("m_value", 8, 0x60)], []);
        (string Name, string Type, int Container, int Index)[] outer = [("Inner", "ESEntity", 0, 0)];
        (string Name, string Type, int Container, int Index)[] inner = [("Value", "int", 0, 0)];
        var reader = new WideNativeParams(schema);
        reader.Walker.UseEntitySchemas(EntitySchemaReader.Read(Latest(true, (SchemaA, outer), (SchemaB, inner)), schema)!,
            passUnknown: true);
        var block = HostBlock(w => { w.Write(0xFFFFFFFFu); w.Write(SchemaB.ToByteArray()); }, behind: w => w.Write(123));

        var walk = reader.Walker.Run(block, 0, block.Length, HostId);

        Assert.True(walk.Exact, walk.Error);
        Assert.Equal(["ESEntityCell", "Model", "ESBlob", "SymbolInfo", "ESBlob"], walk.Queue.Select(id => reader.Walker.Class(id)!.Name));
        Assert.Equal("FieldName", reader.OwnName(walk, HostId));
        Assert.False(reader.Walker.Run(block[..^1], 0, block.Length - 1, HostId).Exact);
    }

    [Fact]
    public void Walk_AnEntityWithTheHandle0_HasNoValues_AndQueuesNothing()
    {
        var block = HostBlock(null, handle: 0);
        var walk = Walker((SchemaA, TextAndIntFields)).Run(block, 0, block.Length, HostId);

        Assert.True(walk.Exact, walk.Error);
        Assert.Equal([CellId, ModelId, DataId], walk.Queue);
        Assert.Equal("FieldName", walk.Captures.Single(c => c.Key == "Data.m_name").Text);
        Assert.Equal(-1, walk.EmptyEntityAt);
    }

    [Fact]
    public void Walk_StopsAtAnEntityWhoseSchemaIsNotStored()
    {
        var block = HostBlock(TextAndInt);
        var walk = Walker((SchemaB, FieldsB)).Run(block, 0, block.Length, HostId);

        Assert.Equal(("entity schema not stored", "ESEntity.m_blob", "ESEntityCell"), (walk.Error, walk.StopField, walk.StopClass));
        // the same with no schema at all: the set that is given when the schemas of a file cannot be read
        Assert.Equal("entity schema not stored", Walker(Schema(), new Dictionary<Guid, SchemaField[]?>()).Run(block, 0, block.Length, HostId).Error);
    }

    [Fact]
    public void Walk_PassesAnEntityItCannotRead_AsWithoutTheSchemas_WhenTheReadersAskForIt()
    {
        var block = HostBlock(TextAndInt);
        var without = Walker(Schema(), null).Run(block, 0, block.Length, HostId);
        foreach (var schemas in new[] { EntitySchemaReader.Read(Latest(false, (SchemaB, FieldsB)), Schema())!,
                     EntitySchemaReader.Read(Latest(false, (SchemaA, [("Other", "mystery", 0, 0)])), Schema())!, new Dictionary<Guid, SchemaField[]?>() })
        {
            var walker = new WideBlockWalker(Schema(), (declaring, field) => declaring.Name + "." + field.Name, []);
            walker.UseEntitySchemas(schemas, passUnknown: true);
            var walk = walker.Run(block, 0, block.Length, HostId);

            // the schema of the entity is not stored, or its field type is not read: the walk goes on as without the schemas
            Assert.Equal((without.Exact, without.Error, without.Position, without.EmptyEntityAt), (walk.Exact, walk.Error, walk.Position, walk.EmptyEntityAt));
            Assert.Equal(without.Queue, walk.Queue);
        }
        // an entity it can read is read by its schema also then
        var reading = new WideBlockWalker(Schema(), (declaring, field) => declaring.Name + "." + field.Name, []);
        reading.UseEntitySchemas(EntitySchemaReader.Read(Latest(false, (SchemaA, TextAndIntFields)), Schema())!, passUnknown: true);
        Assert.True(reading.Run(block, 0, block.Length, HostId).Exact);
    }

    [Fact]
    public void Walk_StopsAtAHandleOfAnEntityThatIsNeither0NorAllOnes()
    {
        var block = HostBlock(TextAndInt, handle: 1);
        var walk = Walker((SchemaA, TextAndIntFields)).Run(block, 0, block.Length, HostId);

        Assert.Equal(("entity handle", "ESEntity.m_blob", "ESEntityCell"), (walk.Error, walk.StopField, walk.StopClass));
    }

    [Fact]
    public void Walk_StopsAtAnEntityOfASchemaWithAFieldTypeThatIsNotRead()
    {
        var block = HostBlock(TextAndInt);
        var walk = Walker((SchemaA, [("Count", "mystery", 0, 1), ("Name", "TCHAR", 0, 0)])).Run(block, 0, block.Length, HostId);

        Assert.Equal(("entity field type", "ESEntity.m_blob", "ESEntityCell"), (walk.Error, walk.StopField, walk.StopClass));
    }

    [Fact]
    public void Walk_AStopInsideTheValuesOfAnEntity_NamesTheFieldOfItsSchema()
    {
        var block = HostBlock(w => Text(w, "Stored"))[..^38];               // cut behind the text of the entity: the 38 bytes of the data are gone
        var walk = Walker((SchemaA, TextAndIntFields)).Run(block, 0, block.Length, HostId);

        Assert.Equal(("end of payload", "ESBlob.Count", "ESBlob", 2), (walk.Error, walk.StopField, walk.StopClass, walk.StopQueueIndex));
    }

    [Fact]
    public void Walk_WithoutEntitySchemas_PassesTheEntityAsBefore()
    {
        var block = HostBlock(TextAndInt);
        var walk = Walker(Schema(), null).Run(block, 0, block.Length, HostId);

        // nothing is queued for the entity, so its values are read as the object behind them
        Assert.False(walk.Exact);
        Assert.Null(walk.Error);
        Assert.Equal(0, walk.EmptyEntityAt);
        Assert.Equal([CellId, ModelId, DataId], walk.Queue);
        Assert.Equal((WideBlockWalker.CaptureKind.PointerClass, -1), (walk.Captures.Single(c => c.Key == "ESEntity.m_blob").Kind, walk.Captures.Single(c => c.Key == "ESEntity.m_blob").Integer));
        Assert.Equal("Stored", walk.Captures.Single(c => c.Key == "Data.m_name").Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Read_GivesTheFieldsOfEveryStoredSchemaInEntryOrder_FromEitherHolderOfTheStorage(bool usageMap)
    {
        var schema = Schema(wideIds: true, usageMap);

        var repeated = new Guid("33333333-3333-3333-3333-333333333333");
        var schemas = EntitySchemaReader.Read(Latest(usageMap, (SchemaA, FieldsA), (SchemaB, [("Other", "mystery", 0, 0)]),
            (repeated, [("First", "int", 0, 0), ("Second", "int", 0, 0)])), schema);

        Assert.NotNull(schemas);
        Assert.Equal(3, schemas.Count);
        // two fields with one entry index give no order to read the values by
        Assert.Null(schemas[repeated]);
        Assert.Equal(
            new (string, int, int, int?)[]
            {
                ("Name", 8, 0x60, null), ("Count", 4, 0, null), ("Values", 7, 0x50, null), ("Owner", 14, 0, ElementIdId), ("Map", 14, 0x50, TextIntId),
                ("Inner", 14, 0, EntityId),
            },
            schemas[SchemaA]!.Select(f => (f.Name, (int)f.Type, (int)f.Mods, f.Ref)));
        // a type name that is neither a scalar nor a class of the class schema: the schema is known, its entities are not read
        Assert.Null(schemas[SchemaB]);
    }

    [Fact]
    public void Read_GivesNothing_WhenTheDocumentIsNotWalkedWithoutAnError()
    {
        var schema = Schema();
        byte[] latest = Latest(false, (SchemaA, FieldsA));

        Assert.NotNull(EntitySchemaReader.Read(latest, schema));
        Assert.Null(EntitySchemaReader.Read(latest[..^8], schema));                 // cut inside the GUID of the schema
        Assert.Null(EntitySchemaReader.Read(HostBlock(null, handle: 0), schema));   // not an ADocument
        // items that take no bytes cost two steps each: the walk ends at the step budget of a record walk of the census (16 per byte and 65,536)
        var loose = new SchemaDocument { Classes = [new(1, "Mark", 0, 1, [], []), new(2, "ADocument", 0, 1, [Field("m_marks", 14, 0x50, 1)], [])] };
        Assert.NotNull(EntitySchemaReader.Read(Block(2, w => w.Write(30_000u)), loose));
        Assert.Null(EntitySchemaReader.Read(Block(2, w => w.Write(1_000_000u)), loose));
        // an entity of the document itself: no schema is known while the schemas are read, so one with values stops the walk
        // also where its GUID repeats the key in front of it; a handle of 0 has no values and is passed
        var own = new SchemaDocument
        {
            Classes = [.. schema.Classes.Where(c => c.Id != DocumentId),
                new(DocumentId, "ADocument", 0, 1, [Field("m_entities", 14, 0, CellId), Field("m_pStorage", 14, 1)], [])],
        };
        byte[] Own(uint handle) => Block(DocumentId, w =>
        {
            w.Write(1u); w.Write(SchemaA.ToByteArray()); w.Write(handle);   // m_entities: one entity, its key, the handle
            if (handle != 0) w.Write(SchemaA.ToByteArray());
            w.Write(0xFFFFFFFFu); w.Write((ushort)StorageId);               // m_pStorage
            w.Write(0u); w.Write(0u);                                       // no schema, and the four bytes behind the last object
        });
        Assert.NotNull(EntitySchemaReader.Read(Own(0), own));
        Assert.Null(EntitySchemaReader.Read(Own(0xFFFFFFFF), own));
    }
}
