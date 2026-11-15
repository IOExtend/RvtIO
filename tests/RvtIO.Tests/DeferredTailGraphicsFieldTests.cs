using RvtIO.Model;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

/// <summary>Layouts of graphics fields inside the deferred tail walk of ExtractParams. Every object is probed by a
/// synthetic root (ContentElemRecPointers) whose header pointer queues it, followed by an AnalyticalSettingsCell: the
/// cell's byte is read (key 308:m_enableAnalyticalModel) only when the object before it consumed exactly its bytes. The
/// room calculation point of a regen data object is read through the same root, without a cell behind it.</summary>
public class DeferredTailGraphicsFieldTests
{
    private const int GGroup = 2009, PointOnSketchData = 3027, AnalyticalSettingsCell = 308;
    private const string CellKey = "308:m_enableAnalyticalModel";

    [Fact]
    public void GNodeInfoIsFourInlineWordsNotATaggedNull()
    {
        // GInfo words (0,0,0,0) were read as a 4-byte null, which took the next word as the m_subNodes count.
        var tail = Concat(Words(0, 0, 0, 0), Words(1, 5), Tag(AnalyticalSettingsCell), Cell(1));
        Assert.Equal(1, Read(GGroup, tail)!.Fields[CellKey]);
    }

    [Fact]
    public void RoomCalculationPointRegenDataIsThreeDoublesKeptUnderTheKeyOfItsField()
    {
        // SpatialElementCalculationPointRegenData (3639) has one field of its own, the XYZ m_position: 24 bytes.
        var tail = Concat(BitConverter.GetBytes(-0.5), BitConverter.GetBytes(0.25), BitConverter.GetBytes(5.375));
        var result = Read(3639, tail)!;
        Assert.True(result.TailWalkComplete);
        Assert.Equal(-0.5, result.DoubleFields["3639:m_position@0"]);
        Assert.Equal(0.25, result.DoubleFields["3639:m_position@1"]);
        Assert.Equal(5.375, result.DoubleFields["3639:m_position@2"]);
    }

    [Fact]
    public void GNodeInfoWithTheKnownFlagPatternKeepsItsSixteenBytes()
    {
        var tail = Concat(Words(-1, 0, -1, 0x00080004), Words(1, 5), Tag(AnalyticalSettingsCell), Cell(1));
        Assert.Equal(1, Read(GGroup, tail)!.Fields[CellKey]);
    }

    [Fact]
    public void TruncatedGNodeInfoKeepsTheRootAndCapturesNothingFromTheTail()
    {
        var result = Read(GGroup, Words(0, 0, 0)); // 12 of 16 bytes
        Assert.NotNull(result);
        Assert.DoesNotContain(CellKey, result.Fields.Keys);
    }

    [Fact]
    public void AlternateHostGeomRefIsThirtyThreeInlineBytes()
    {
        // PointOnSketchData: m_hostTrfCreator (queues the cell), inline GeomRef, offset, uv[2], sketch plane.
        byte[] geomRef = Concat(Words(0, 0, -1, -1, -1, -1, 0, -1), new byte[] { 0 });
        Assert.Equal(33, geomRef.Length);
        var tail = Concat(Words(77), Tag(AnalyticalSettingsCell), geomRef, new byte[8 + 16], Words(-1), Cell(1));
        Assert.Equal(1, Read(PointOnSketchData, tail)!.Fields[CellKey]);
    }

    [Fact]
    public void GeomRefWithIntermediateTagsAndNextRefStaysAligned()
    {
        // Two tags (8 bytes) and a non-null next reference queue the cell class ahead of the final cell.
        byte[] geomRef = Concat(Words(2, 10, 11), Words(9), Tag(AnalyticalSettingsCell), Words(-1, -1, -1, -1, 0, -1), new byte[] { 0 });
        var tail = Concat(Words(77), Tag(AnalyticalSettingsCell), geomRef, new byte[8 + 16], Words(-1),
            Cell(0), Cell(1));
        var result = Read(PointOnSketchData, tail)!;
        Assert.Equal(1, result.Fields[CellKey]); // the last cell read wins; the earlier one is 0
    }

    [Fact]
    public void GeomRefSchemaIsChecked()
    {
        var geomRef = TfBlockDeserializer.Schemas[140];
        Assert.Equal("GeomRef", geomRef.Name);
        Assert.Equal(9, geomRef.Fields.Count);
        var reference = TfBlockDeserializer.SerializationFields(139)!.Single(f => f.Name == "m_alternateHostGRef");
        Assert.Null(reference.Ref); // the schema leaves the type untyped; the override supplies GeomRef
    }

    [Fact]
    public void TableValueOutsideItsDomainRestoresThePreviousDefault()
    {
        var element = Applied(categoryId: -2001330, value: 1, previous: (StorageType.Integer, 0));
        Prune(element, 3);
        Assert.Equal(0, element.Parameters.Single().Value); // floor-like element: the cell value is not its parameter
    }

    [Fact]
    public void TableValueOutsideItsDomainIsRemovedWhenItCreatedTheParameter()
    {
        var element = Applied(categoryId: -2000032, value: 1, previous: null);
        Prune(element, 3);
        Assert.Empty(element.Parameters);
    }

    [Fact]
    public void TableValueInsideItsDomainIsKept()
    {
        var element = Applied(categoryId: -2001330, value: 1, previous: (StorageType.Integer, 0));
        Prune(element, 804);
        Assert.Equal(1, element.Parameters.Single().Value);
    }

    [Fact]
    public void ValueChangedAfterTheTableIsNotRolledBack()
    {
        var element = Applied(categoryId: -2001330, value: 1, previous: (StorageType.Integer, 0));
        element.Parameters.Single().Value = 7;
        Prune(element, 3);
        Assert.Equal(7, element.Parameters.Single().Value);
    }



    [Fact]
    public void LargestMotorPairAndElementIdPairsAreInlineLeaves()
    {
        // ElectricalFamInstDesignPropertyManager: m_LargestMotorMap = i32, i32, f64; a non-empty m_LC*ParamMap
        // holds pair<ElementId, ElementId> entries of two bare ids (the schema points .second back at the pair).
        byte[] manager = Concat(
            Words(-1), Words(0), Words(-1, -1, -1, -1), new byte[] { 0 }, Words(0, 0, 0), // 750, 1286 up to the strings
            Words(0), Words(0),                                                              // tapCoefficients, m_LCLoadMap
            Words(-1, -1), new byte[8],                                                      // m_LargestMotorMap
            Words(1, 5, 6), Words(0, 0, 0, 0),                                               // five maps, the first with one pair
            new byte[32], Words(0));                                                          // four doubles, numbering option
        Assert.Equal(1, ReadList(1285, manager, Cell(1))!.Fields[CellKey]);
    }

    [Fact]
    public void StepWithTwoWordEmptyReverseTableAndWallJoinTweakFields()
    {
        // Tables empty (FFFFFFFF x3), reverse = [0][0], id 1, version 3, flags, owner handle 2; then
        // m_affectedEdgesTags = [ElementId][int[] wrapper], m_idGeomJoined, m_healingParents.
        byte[] step = Concat(Words(-1, -1, -1), Words(0, 0), Words(1, 3, 0x0B9F7D, 2),
            Words(1, 5, 2, 70, 71), Words(1, 6), Words(0));
        Assert.Equal(1, ReadList(4147, step, Cell(1))!.Fields[CellKey]);
    }

    [Fact]
    public void StepWithThreeWordEmptyReverseTable()
    {
        byte[] step = Concat(Words(-1, -1, -1), Words(-1, 0, 0), Words(1, 3, 0x0B9F7D, 2));
        Assert.Equal(1, ReadList(932, step, Cell(1))!.Fields[CellKey]);
    }

    [Fact]
    public void StepWithFourWordEmptyReverseTable()
    {
        // Sketch-regeneration steps: three empty tables, then [FFFFFFFF][FFFFFFFF][0][0] before the generator id.
        byte[] step = Concat(Words(-1, -1, -1), Words(-1, -1, 0, 0), Words(1, 0, 0x3D, 2));
        Assert.Equal(1, ReadList(3411, step, Cell(1))!.Fields[CellKey]);
    }

    [Fact]
    public void ChanceOwnerWordInTheVersionSlotDoesNotShortenTheReverseTable()
    {
        // Version 2 puts the owner handle's value where a one-word-shorter reading expects it; that reading has
        // m_id 0, while generator ids start at 1. Taking it would shift the next object by eight bytes.
        byte[] step = Concat(Words(-1, -1, -1), Words(0, 0), Words(1, 2, 0x19F3D, 2));
        Assert.Equal(1, ReadList(932, step, Cell(1))!.Fields[CellKey]);
    }

    [Fact]
    public void CountedAndTerminatedFaceTablesReachingTheSameTailPreferTheCounted()
    {
        // Level plane step: one 16-byte face record, then empty curve, edge and reverse words, id 1.
        byte[] step = Concat(Words(1, 0, 6, 0, -1), Words(0, 0, 0), Words(1, 0, 1, 2));
        Assert.Equal(1, ReadList(1053, step, Cell(1))!.Fields[CellKey]);
    }

    [Fact]
    public void RoomFaceOffsetEntriesAreAnIntAndFourDoubles()
    {
        byte[] step = Concat(Words(-1, -1, -1), Words(0), Words(1, 1, 0x0B9F7D, 2),
            Words(2), Words(2), new byte[32], Words(3), new byte[32]);
        Assert.Equal(1, ReadList(3468, step, Cell(1))!.Fields[CellKey]);
    }

    [Fact]
    public void SharedExpressionPointersOfVariationalSketchObjectsCarryAClassTag()
    {
        // VarSketchLineSegObj: m_params (nibble 4) = [handle][tag 4064 VarParam] x2, owner back-reference, id,
        // angle coefficient, unbounded flag; the queued VarParam objects (ref count + value) follow the cell.
        byte[] segment = Concat(Words(2), Words(3), Tag(4064), Words(4), Tag(4064), Words(2, 9), new byte[8], new byte[] { 0 });
        Assert.Equal(1, ReadList(4086, segment, Cell(1))!.Fields[CellKey]);
    }

    [Fact]
    public void RoomBoundingItemLinkRefIsABareElementIdInTheTailWalk()
    {
        // SegmentKey: m_item = RoomBoundingItem (host/link instance id + linked id; the schema makes the second a
        // self-reference), m_majorIndex, m_subIndex.
        byte[] key = Words(40, 41, 3, 4);
        Assert.Equal(1, ReadList(2978, key, Cell(1))!.Fields[CellKey]);
    }

    [Fact]
    public void DividedFaceStepReadsItsCellAndEdgeSegmentMaps()
    {
        // CGridDivideFaceGStep: m_cellMap = pair<CGCellCoords, CGCellContents> (u, v, edge tags, face tag, 5 ids),
        // m_edgeToOriginalEdgeMap = pair<int, int>, m_originalToEdgeSegs = pair<int, CEdgeSegSetWrapper>.
        byte[] step = Concat(Words(-1, -1, -1), Words(0, 0), Words(1, 3, 0, 2),
            Words(-1),
            Words(1), Words(0, 0), Words(2, 0x58, 0x5F), Words(0x57), Words(-1, -1, -1, -1, -1),
            Words(1), Words(7, 8),
            Words(1), Words(5), Words(1), new byte[16], Words(9));
        Assert.Equal(1, ReadList(590, step, Cell(1))!.Fields[CellKey]);
    }

    [Fact]
    public void ConnectorReferencesAreInlineConnectorIds()
    {
        // Connector: m_arrRefs = ConnectorId entries (ElementId, index, type), owner back-reference, index, mode,
        // m_modifiers = one owned pointer; its cell follows the root's own cell in the queue.
        byte[] connector = Concat(Words(1, 0x60169, 1, 1), Words(2), Words(1), Words(1), Words(1, -1), Tag(AnalyticalSettingsCell));
        Assert.Equal(1, ReadList(771, connector, Concat(Cell(1), Cell(1)))!.Fields[CellKey]);
    }

    [Theory]
    [InlineData(0, 1, 1)] [InlineData(1, 0, 1)] [InlineData(0, 0, 0)] [InlineData(1, 1, 1)]
    public void TopAttachmentIsTheFieldOfAnyCutPairStepNotOfTheLastOne(int first, int second, int expected)
    {
        var target = new TfBlockDeserializer.NativeParams();
        int before = 0;
        foreach (int value in new[] { first, second })
        {
            before = target.Fields.GetValueOrDefault("977:m_isTop");
            target.Fields["977:m_isTop"] = value; // the step's own read
            TfBlockDeserializer.KeepAnyAttachTop(target, 977, before);
        }
        Assert.Equal(expected, target.Fields["977:m_isTop"]);
    }

    [Theory]
    [InlineData(0, 1, 1)] [InlineData(1, 0, 1)] [InlineData(0, 0, 0)]
    public void TopAttachmentOfAWallIsTheFieldOfAnyJoinToRoofStepNotOfTheLastOne(int first, int second, int expected)
    {
        var target = new TfBlockDeserializer.NativeParams();
        foreach (int value in new[] { first, second })
        {
            int before = target.Fields.GetValueOrDefault("2262:m_baseOrTop");
            target.Fields["2262:m_baseOrTop"] = value;
            TfBlockDeserializer.KeepAnyAttachTop(target, 2262, before);
        }
        Assert.Equal(expected, target.Fields["2262:m_baseOrTop"]);
    }

    [Fact]
    public void OtherStepClassesDoNotTouchTheTopAttachmentKey()
    {
        var target = new TfBlockDeserializer.NativeParams();
        target.Fields["977:m_isTop"] = 0;
        TfBlockDeserializer.KeepAnyAttachTop(target, 978, 1);
        Assert.Equal(0, target.Fields["977:m_isTop"]);
    }

    [Fact]
    public void StairsSupportHostReferenceIsFourDoublesAndAGeomRef()
    {
        // StairsSupport root: Element fields (the geometry-steps pointer queues the cell), its own fields up to the two
        // flags, then m_hostRef = 4 x f64 + GeomRef (empty tags, element id, foreign -1, tag, sub -1, flags, view -1, lazy).
        byte[] element = Concat(Words(0, 0, 0, 0), Words(11), Tag(AnalyticalSettingsCell), Words(0), Words(0), Words(0), Words(0),
            Words(100, -1, -1, -1, -1, -1, -1, -1), new byte[3]);
        byte[] hostRef = Concat(new byte[32], Words(0, 0, 354390, -1, 674, -1, 0, -1), new byte[] { 0 });
        byte[] ownFields = new byte[98]; // path loop, 4 boundary loops, faces, offsets, join arrays, ids, cuts, type, flags (all zero)
        byte[] block = Concat(element, ownFields, hostRef, Cell(1));
        Assert.Equal(1, ReadBlock(3727, block)!.Fields[CellKey]);
    }

    [Fact]
    public void SharedExpressionPointerNibbleIsUsedOnlyByTheVariationalSketchFields()
    {
        var users = TfBlockDeserializer.Schemas.SelectMany(kv => kv.Value.Fields.Select(f => (kv.Key, f)))
            .Where(x => x.f.Type == 14 && (x.f.Mods & 0x0F) == 4).Select(x => (x.Key, x.f.Name)).ToList();
        Assert.Equal([(4045, "m_args"), (4067, "m_dimValue"), (4067, "m_refPts"), (4069, "m_params")],
            users.OrderBy(x => x.Key).ThenBy(x => x.Name).ToList());
    }


    private static Element Applied(int categoryId, int value, (StorageType, object?)? previous)
    {
        var element = new Element { Id = 10, CategoryId = categoryId };
        element.Parameters.Add(new Parameter { Id = -1001552, StorageType = StorageType.Integer, Value = value });
        element.NativeTableAddedParams = new() { (-1001552, new[] { "1794", "804" }, StorageType.Integer, value, false) };
        if (previous is null) element.NativeTableCreatedParams = new() { -1001552 };
        else element.NativeTablePrevious = new() { [-1001552] = (previous.Value.Item2, previous.Value.Item1, false) };
        return element;
    }

    private static void Prune(Element element, ushort rootClass)
    {
        var info = new RevitFileInfo { Elements = { element } };
        ElementBuilder.PruneNativeFieldTableOutsideDomains(info, new Dictionary<uint, ushort> { [10] = rootClass });
    }

    /// <summary>Root = GeomStepList: m_nonBRepGList queues the object under test and the cell; the other five lists
    /// are empty, the four snapshots null.</summary>
    private static TfBlockDeserializer.NativeParams? ReadList(ushort objectClass, byte[] objectBytes, byte[] cell)
    {
        byte[] root = Concat(Words(2), Words(11), Tag(objectClass), Words(12), Tag(AnalyticalSettingsCell),
            Words(0, 0, 0, 0, 0), Words(0, 0, 0, 0), Words(2), new byte[20], Words(0), new byte[] { 0 });
        byte[] payload = Concat(root, objectBytes, cell);
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        uint size = (uint)payload.Length + 2;
        writer.Write(100); writer.Write(0u); writer.Write(size); writer.Write((ushort)2086);
        writer.Write(payload); writer.Write(size);
        var block = stream.ToArray();
        return TfBlockDeserializer.ExtractParams(block, 0, BitConverter.ToUInt32(block, 8), 2086);
    }

    private static TfBlockDeserializer.NativeParams? Read(ushort tailClass, byte[] tail)
    {
        // ContentElemRecPointers (814): header pointer (handle 44, class tag), two compact words, element id, episode.
        byte[] payload = Concat(Words(44), Tag(tailClass), new byte[16], Words(7, 8), tail);
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        uint size = (uint)payload.Length + 2;
        writer.Write(100); writer.Write(0u); writer.Write(size); writer.Write((ushort)814);
        writer.Write(payload); writer.Write(size);
        var block = stream.ToArray();
        return TfBlockDeserializer.ExtractParams(block, 0, BitConverter.ToUInt32(block, 8), 814);
    }

    private static TfBlockDeserializer.NativeParams? ReadBlock(ushort rootClass, byte[] payload)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        uint size = (uint)payload.Length + 2;
        writer.Write(100); writer.Write(0u); writer.Write(size); writer.Write(rootClass);
        writer.Write(payload); writer.Write(size);
        var block = stream.ToArray();
        return TfBlockDeserializer.ExtractParams(block, 0, BitConverter.ToUInt32(block, 8), rootClass);
    }

    private static byte[] Cell(byte enabled) => Concat(Words(-1, 1), new[] { enabled });
    private static byte[] Tag(int cls) => BitConverter.GetBytes((ushort)cls);
    private static byte[] Words(params long[] words) => words.SelectMany(w => BitConverter.GetBytes(unchecked((uint)w))).ToArray();
    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();
}
