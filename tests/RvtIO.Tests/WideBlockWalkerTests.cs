using System.Text;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

/// <summary>The schema-driven field walk of an element block with 64-bit ids (<see cref="WideBlockWalker"/>): hand-made blocks in the
/// grammar of Revit 2025 files, then Test_2025.rvt against the current profile (names and walk coverage).</summary>
public class WideBlockWalkerTests
{
    private const int IdentifierId = 1, ElementIdId = 2, StoreId = 3, BaseId = 4, DerivedId = 5;

    private static SchemaField Field(string name, byte type, byte mods = 0, int? reference = null, int? fixedCount = null) =>
        new(name, type, mods, 0, fixedCount, reference, null, null);

    private static SchemaDocument Schema() => new()
    {
        Classes =
        [
            new(IdentifierId, "Identifier", 0, 2, [Field("m_id64", 11)], []),
            new(ElementIdId, "ElementId", 0, 1, [Field("m_id", 14, 0, IdentifierId)], []),
            new(StoreId, "Store", 0, 1, [Field("m_value", 4)], []),
            new(BaseId, "Base", 0, 1,
                [Field("m_pStore", 14, 1), Field("m_id", 14, 0, ElementIdId), Field("m_flag", 1)], []),
            new(DerivedId, "Derived", BaseId, 1,
                [Field("m_name", 8, 96), Field("m_point", 7, 16, null, 3), Field("m_ids", 14, 80, ElementIdId),
                 Field("m_back", 14, 3), Field("m_short", 3)], []),
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

    private static byte[] DerivedBlock(bool withStore, bool unknownTag = false) => Block(DerivedId, w =>
    {
        if (withStore) { w.Write(0xFFFFFFFFu); w.Write((ushort)(unknownTag ? 999 : StoreId)); } else w.Write(0u);   // m_pStore
        w.Write(4711L);                                                     // m_id (64 bits)
        w.Write((byte)1);                                                   // m_flag
        Text(w, "Wall");                                                    // m_name
        w.Write(1.5); w.Write(2.5); w.Write(3.5);                           // m_point
        w.Write(2u); w.Write(10L); w.Write(-1L);                            // m_ids
        w.Write(1u);                                                        // m_back
        w.Write((short)7);                                                  // m_short (2 bytes)
        if (withStore) w.Write(42);                                         // the queued Store
    });

    private static WideBlockWalker Walker(IEnumerable<string>? records = null) =>
        new(Schema(), (declaring, field) => declaring.Name + "." + field.Name, records ?? []);

    [Theory]
    [InlineData(150_000u, true)]            // longer than any fixed limit, but in the body: read
    [InlineData(150_001u, false)]           // one character more than the body holds
    [InlineData(0x80000000u, false)]        // twice the count overflows 32 bits
    [InlineData(0xFFFFFFFFu, false)]
    public void Walk_ReadsATextAsLongAsItsCount_AndStopsAtOneLongerThanTheBody(uint count, bool exact)
    {
        var schema = new SchemaDocument { Classes = [new(1, "Note", 0, 1, [Field("m_text", 8, 0x60)], [])] };
        var block = Block(1, w => { w.Write(count); w.Write(new byte[2 * 150_000]); });
        var walk = new WideBlockWalker(schema, (declaring, field) => declaring.Name + "." + field.Name, []).Run(block, 0, block.Length, 1);

        Assert.Equal(exact, walk.Exact);
        Assert.Equal(exact ? null : "end of payload", walk.Error);
    }

    [Fact]
    public void Walk_ReadsAClassReferenceAsATwoByteClassTag()
    {
        // the shape of ElementHeader.m_classDef: an inline structure whose only field is a class reference (type 10)
        var schema = new SchemaDocument
        {
            Classes =
            [
                new(1, "ClassDefinitionRef", 0, 1, [Field("m_ref", 10)], []),
                new(2, "Header", 0, 1, [Field("m_classDef", 14, 0, 1), Field("m_next", 4)], []),
            ],
        };
        var block = Block(2, w => { w.Write((ushort)0x1234); w.Write(77); });
        var walk = new WideBlockWalker(schema, (declaring, field) => declaring.Name + "." + field.Name, []).Run(block, 0, block.Length, 2);

        Assert.True(walk.Exact, walk.Error);
        var captures = walk.Captures.ToDictionary(c => c.Key);
        Assert.Equal(0x1234, captures["ClassDefinitionRef.m_ref"].Integer);
        Assert.Equal(77, captures["Header.m_next"].Integer);
    }

    [Fact]
    public void Walk_ConsumesAllFieldKindsToTheLastByte()
    {
        var block = DerivedBlock(withStore: true);
        var walk = Walker().Run(block, 0, block.Length, DerivedId);
        Assert.True(walk.Exact, walk.Error);
        Assert.Equal([StoreId], walk.Queue);
        var captures = walk.Captures.ToDictionary(c => c.Key);
        Assert.Equal(4711, captures["Base.m_id"].Integer);
        Assert.Equal(1, captures["Base.m_flag"].Integer);
        Assert.Equal("Wall", captures["Derived.m_name"].Text);
        Assert.Equal(2.5, captures["Derived.m_point@1"].Real);
        Assert.Equal(2, captures["Derived.m_ids#"].Integer);
        Assert.Equal(StoreId, captures["Base.m_pStore"].Integer);
        Assert.Equal(7, captures["Derived.m_short"].Integer);
        Assert.Equal(42, captures["Store.m_value"].Integer);
        var backReference = captures["Derived.m_back"];
        Assert.Equal(1, backReference.Integer);
        Assert.Equal(1u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(backReference.ValueOffset)));
    }

    [Fact]
    public void Walk_RecordsWhereEachCapturedValueStartsAndWhereAQueuedObjectIsExpected()
    {
        var block = DerivedBlock(withStore: true);
        var walk = Walker().Run(block, 0, block.Length, DerivedId);
        var captures = walk.Captures.ToDictionary(c => c.Key);
        Assert.Equal(4711L, BitConverter.ToInt64(block, captures["Base.m_id"].ValueOffset));
        Assert.Equal(1, block[captures["Base.m_flag"].ValueOffset]);
        Assert.Equal(2.5, BitConverter.ToDouble(block, captures["Derived.m_point@1"].ValueOffset));
        Assert.Equal(42, BitConverter.ToInt32(block, captures["Store.m_value"].ValueOffset));

        // A text starts at its length, an array at its count.
        var name = captures["Derived.m_name"];
        Assert.Equal(4u, BitConverter.ToUInt32(block, name.ValueOffset));
        Assert.Equal("Wall", Encoding.Unicode.GetString(block, name.ValueOffset + sizeof(uint), 8));
        Assert.Equal(2u, BitConverter.ToUInt32(block, captures["Derived.m_ids#"].ValueOffset));

        // A pointer starts at its handle and names the position of its object in the queue.
        var store = captures["Base.m_pStore"];
        Assert.Equal(uint.MaxValue, BitConverter.ToUInt32(block, store.ValueOffset));
        Assert.Equal(0, store.QueuePosition);
        Assert.Equal(StoreId, walk.Queue[store.QueuePosition]);

        // A capture the walker makes without reading a field of its own has no offset.
        Assert.All(walk.Captures.Where(c => c.Key.EndsWith("m_short")), c => Assert.Equal(-1, c.ValueOffset));
    }

    [Fact]
    public void Walk_WithRootOnlyReadsTheFieldsOfTheElementAndStopsBeforeItsQueuedObjects()
    {
        var block = DerivedBlock(withStore: true);
        var root = Walker().Run(block, 0, block.Length, DerivedId, rootOnly: true);
        Assert.True(root.RootRead);
        Assert.False(root.Exact);
        Assert.Null(root.Error);
        Assert.Equal([StoreId], root.Queue);
        var captures = root.Captures.ToDictionary(c => c.Key);
        Assert.Equal(4711, captures["Base.m_id"].Integer);
        Assert.Equal(7, captures["Derived.m_short"].Integer);
        Assert.DoesNotContain("Store.m_value", captures.Keys);

        var full = Walker().Run(block, 0, block.Length, DerivedId);
        Assert.True(full.Exact, full.Error);
        Assert.True(full.Captures.Count > root.Captures.Count);
    }

    [Fact]
    public void Walk_CapturesTheGuidOfAnExternalParameterKeyAsText()
    {
        var guid = new Guid("50b3ce53-fa73-47a1-ae3e-75035a1f17dd");
        var schema = new SchemaDocument
        {
            Classes =
            [
                new(1, "GUIDvalue", 0, 1, [Field("m_guid", 9)], []),
                new(2, "ExternalParamKey", 0, 1, [Field("m_guidValue", 14, 0, 1)], []),
                new(3, "Holder", 0, 1, [Field("m_key", 14, 0, 2), Field("m_next", 4)], []),
            ],
        };
        var block = Block(3, w => { w.Write(guid.ToByteArray()); w.Write(77); });
        var walk = new WideBlockWalker(schema, (declaring, field) => declaring.Name + "." + field.Name, []).Run(block, 0, block.Length, 3);

        Assert.True(walk.Exact, walk.Error);
        var key = Assert.Single(walk.Captures, c => c.Key == "Holder.m_key");
        Assert.Equal(CaptureKindText, key.Kind);
        Assert.Equal(guid.ToString(), key.Text);
        Assert.Equal(77, Assert.Single(walk.Captures, c => c.Key == "Holder.m_next").Integer);
    }

    private const WideBlockWalker.CaptureKind CaptureKindText = WideBlockWalker.CaptureKind.Text;

    [Fact]
    public void RoomPhase_IsReadWhenTheFieldIsACapturedKey()
    {
        const int roomId = 3;
        var schema = new SchemaDocument
        {
            Classes =
            [
                new(IdentifierId, "Identifier", 0, 2, [Field("m_id64", 11)], []),
                new(ElementIdId, "ElementId", 0, 1, [Field("m_id", 14, 0, IdentifierId)], []),
                new(roomId, "RoomElem", 0, 1, [Field("m_phaseId", 14, 0, ElementIdId)], []),
            ],
        };
        var fields = new WideNativeParams(schema);
        var block = Block(roomId, w => w.Write(5L));
        var walk = fields.Walker.Run(block, 0, block.Length, roomId);
        Assert.True(walk.Exact, walk.Error);
        Assert.Equal(5, fields.RoomPhase(walk, roomId));
    }

    [Fact]
    public void Walk_NullPointerQueuesNothing()
    {
        var block = DerivedBlock(withStore: false);
        var walk = Walker().Run(block, 0, block.Length, DerivedId);
        Assert.True(walk.Exact, walk.Error);
        Assert.Empty(walk.Queue);
        Assert.Equal(0, walk.Captures.Single(c => c.Key == "Base.m_pStore").Integer);
    }

    [Theory]
    [InlineData(0xFFFFFFFFu, 0)]       // an empty array: the host type is zero
    [InlineData(0u, 0)]
    [InlineData(2u, 7)]                // the first entry
    public void Walk_FamilyHostType_IsTheFirstEntryOrZeroForAnEmptyArray(uint count, int first)
    {
        const int familyId = 6;
        var schema = new SchemaDocument { Classes = [new(familyId, "Family", 0, 1, [Field("m_refTypeIds", 4, 80)], [])] };
        var block = Block(familyId, w =>
        {
            w.Write(count);
            for (uint i = 0; i < (count == 0xFFFFFFFFu ? 0 : count); i++) w.Write(i == 0 ? 7 : 9);
        });
        var walk = new WideBlockWalker(schema, (declaring, field) => declaring.Name + "." + field.Name, []).Run(block, 0, block.Length, familyId);
        Assert.True(walk.Exact, walk.Error);
        Assert.Equal(first, walk.Captures.Single(c => c.Key == "Family.m_refTypeIds@0").Integer);
    }

    [Fact]
    public void Walk_ShortBlockFailsInsteadOfGuessing()
    {
        var block = DerivedBlock(withStore: true);
        var walk = Walker().Run(block, 0, block.Length - 2, DerivedId);
        Assert.False(walk.Exact);
        Assert.NotNull(walk.Error);
        Assert.True(walk.RootRead);       // the root fields were complete, the queued object was not
    }

    [Fact]
    public void Walk_ClassTagOutsideTheSchemaFails()
    {
        var block = DerivedBlock(withStore: true, unknownTag: true);
        var walk = Walker().Run(block, 0, block.Length, DerivedId);
        Assert.False(walk.Exact);
        Assert.Contains("class tag 999", walk.Error);
        Assert.False(walk.RootRead);
    }

    [Fact]
    public void Walk_RecordClassesAreReturnedInStoredOrder()
    {
        var block = DerivedBlock(withStore: true);
        var walk = Walker(["Store"]).Run(block, 0, block.Length, DerivedId);
        var record = Assert.Single(walk.Records);
        Assert.Equal(StoreId, record.ClassId);
        Assert.Equal(0, record.QueueIndex);
        Assert.Equal(42L, record.Values["m_value"]);
    }

    [Fact]
    public void Walk_ThatStopsInTheRootFields_NamesTheFieldAndTheRootClass()
    {
        var block = DerivedBlock(withStore: true);
        // the payload ends inside the text of m_name: tag 2, m_pStore 6, m_id 8, m_flag 1, text length 4, then 3 of the 8 text bytes
        var walk = Walker().Run(block, 0, 24, DerivedId);
        Assert.Equal("end of payload", walk.Error);
        Assert.Equal("Derived.m_name", walk.StopField);
        Assert.Equal(-1, walk.StopQueueIndex);
        Assert.Equal("Derived", walk.StopClass);
    }

    [Fact]
    public void Walk_ThatStopsInAQueuedObject_NamesItsFieldItsIndexAndItsClass()
    {
        var block = DerivedBlock(withStore: true);
        var walk = Walker().Run(block, 0, block.Length - 2, DerivedId);
        Assert.Equal("end of payload", walk.Error);
        Assert.Equal("Store.m_value", walk.StopField);
        Assert.Equal(0, walk.StopQueueIndex);
        Assert.Equal("Store", walk.StopClass);
    }

    [Fact]
    public void Walk_WithoutAnError_OrStoppedBeforeAnyField_HasNoStopField()
    {
        var block = DerivedBlock(withStore: true);
        var exact = Walker().Run(block, 0, block.Length, DerivedId);
        Assert.Null(exact.StopField);
        Assert.Equal(-1, exact.StopQueueIndex);
        Assert.Null(exact.StopClass);

        var unknown = Walker().Run(block, 0, block.Length, 999);
        Assert.Equal("class 999 has no schema", unknown.Error);
        Assert.Null(unknown.StopField);
        Assert.Equal(-1, unknown.StopQueueIndex);
        Assert.Null(unknown.StopClass);
    }

    // nested: every item holds a field of its own, which must not be named as the stop of the array around it
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public void Walk_StepBudget_StopsACountedArrayOfFieldlessItems_AndTheDefaultChangesNothing(bool limited, bool nested)
    {
        const int emptyId = 1, holderId = 2, pairId = 3;
        var schema = new SchemaDocument
        {
            Classes =
            [
                new(emptyId, "Empty", 0, 1, [], []),
                new(holderId, "Holder", 0, 1, [Field("m_items", 14, 80, nested ? pairId : emptyId)], []),
                new(pairId, "Pair", 0, 1, [Field("a", 14, 0, emptyId)], []),
            ],
        };
        // 1000 items that take no byte: the payload ends right behind the count
        var block = Block(holderId, w => w.Write(1000u));
        var walker = new WideBlockWalker(schema, (_, _) => null, []);
        if (limited) walker.StepBudget = 100;

        var walk = walker.Run(block, 0, block.Length, holderId);

        Assert.Equal(limited ? "step budget" : null, walk.Error);
        Assert.Equal(!limited, walk.Exact);
        Assert.Equal(limited ? "Holder.m_items" : null, walk.StopField);
    }


    private sealed class Test2025Fixture
    {
        public Test2025Fixture()
        {
            var reader = new RvtFileReader();
            Info = reader.Read(TestFileFixture.FindTestFile("Test_2025.rvt"));
            State = reader.LastParsingState!;
        }

        public Model.RevitFileInfo Info { get; }
        public Internal.ParsingState State { get; }
    }

    private static readonly Lazy<Test2025Fixture> Real = new(() => new Test2025Fixture());

    /// <summary>The current profile element names (MeshProbe elems, column elemName) of elements of Test_2025.rvt, one per kind of name owner.</summary>
    [SkippableTheory]
    [InlineData(311u, "Level 1")]
    [InlineData(694u, "Ceiling")]
    [InlineData(23u, "Default")]
    [InlineData(24u, "Default Wall")]
    [InlineData(1049u, "M_Section Head - Filled")]
    [InlineData(1141u, "Metric Callout")]
    [InlineData(1647u, "Overhead Lines")]
    [InlineData(1951u, "Plan Swing")]
    [InlineData(397u, "Exterior - Brick on Mtl. Stud")]
    [InlineData(54538u, "Exterior - Block on Mtl. Stud")]
    [InlineData(195169u, "1")]
    [InlineData(195239u, "2")]
    public void Test2025_OwnNamesEqualReference(uint id, string name)
    {
        Assert.Equal(name, Real.Value.State.WideNames[id]);
    }

    [SkippableFact]
    public void Test2025_EveryElementTableRowHasADecodedRootBlock()
    {
        var state = Real.Value.State;
        // 8,342 of 8,342 root blocks consumed to the last byte; the element table has 8,342 rows
        Assert.Equal(state.ElemTable.Count, state.NativeParams.Count);
        // the stores of a material or property set element sit behind objects of the element (a second set of stores), not in the leading
        // run of the queue: 215 of 8,342 elements
        Assert.InRange(state.NativeParams.Values.Count(np => !np.RootParameterStoresComplete), 1, state.NativeParams.Count / 20);
        Assert.True(state.NativeParams.Values.All(np => np.TailWalkComplete));
        Assert.All(state.NativeParams, pair => Assert.Equal((int)pair.Key, pair.Value.Fields["m_id"]));
    }

    [SkippableFact]
    public void Test2025_DocumentUnitsAreReadFromTheFile()
    {
        var state = Real.Value.State;
        Assert.NotNull(state.OwnDocumentUnitOptions);
        Assert.Equal(136, state.OwnDocumentUnitOptions!.Value.Options.Length);
        Assert.NotNull(state.OwnDocumentUnitConversions);
        Assert.NotNull(Real.Value.Info.SerializedFormatSettings);
    }
}
