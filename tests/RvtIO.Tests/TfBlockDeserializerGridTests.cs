using RvtIO.Internal;
using RvtIO.Model;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

public class TfBlockDeserializerGridTests
{
    [Fact]
    public void FamilyCutLineLoop_OrdersExactClosedProfile()
    {
        var lines = new List<(double[] Start, double[] End)>
        {
            ([-1, -0.5, 2], [-1, -0.5, 0]),
            ([-1, -0.5, 0], [1, -0.5, 0]),
            ([1, -0.5, 0], [1, -0.5, 2]),
            ([1, -0.5, 2], [-1, -0.5, 2]),
        };

        Assert.True(TfBlockDeserializer.TryOrderClosedLineLoop(lines, out var vertices));
        Assert.Equal(4, vertices.Count);
        Assert.Equal([-1, -0.5, 2], vertices[0]);

        lines[3] = ([1, -0.5, 2], [-0.9, -0.5, 2]);
        Assert.False(TfBlockDeserializer.TryOrderClosedLineLoop(lines, out _));
    }

    [Fact]
    public void GeomStepList_DecodesExactWallCutOutInstanceIds()
    {
        var (block, dataSize) = CreateHostCutOutBlock();

        var cuttingElementIds = TfBlockDeserializer.ExtractCuttingElementIds(
            block, 0, dataSize, 363);

        Assert.Equal([1001u, 1002u, 1003u], cuttingElementIds);
    }

    [Fact]
    public void GeomStepList_DecodesAllCachedWallHistoryLayoutsAndIgnoresJoinPairs()
    {
        var (block, dataSize) = CreateCachedHostCutOutBlock();

        var cuttingElementIds = TfBlockDeserializer.ExtractCuttingElementIds(
            block, 0, dataSize, 363, out string? diagnostic);

        Assert.True(cuttingElementIds != null, diagnostic);
        Assert.Equal([2001u, 2002u, 2003u, 2004u], cuttingElementIds);
    }

    [Fact]
    public void GeomStepList_CutPairOnlyPhaseIsAuthoritativelyEmptyForOpenings()
    {
        var (block, dataSize) = CreateCachedHostCutOutBlock(wallCutoutCount: 0);

        var cuttingElementIds = TfBlockDeserializer.ExtractCuttingElementIds(
            block, 0, dataSize, 363, out string? diagnostic);

        Assert.True(cuttingElementIds != null, diagnostic);
        Assert.Empty(cuttingElementIds);
    }

    [Fact]
    public void GeomStepList_ListsCutterAndPairStepsInStepOrder()
    {
        var (block, dataSize) = CreateCachedHostCutOutBlock();

        var cuttingElementIds = TfBlockDeserializer.ExtractCuttingElementIdsAndSteps(
            block, 0, dataSize, 363, out var steps);

        Assert.Equal([2001u, 2002u, 2003u, 2004u], cuttingElementIds);
        Assert.NotNull(steps);
        Assert.Equal([(4137, 2001), (4137, 2002), (4137, 2003), (4137, 2004), (977, 2005)],
            steps.Select(step => ((int)step.ClassId, step.ElementId)));
        // Only a WallCutoutGStepBase step is marked as an inserted-element cutter.
        Assert.Equal([true, true, true, true, false],
            steps.Select(step => step.Cutout));
    }

    [Fact]
    public void GeomStepList_PairOnlyPhaseListsItsPairsAndNoCutters()
    {
        var (block, dataSize) = CreateCachedHostCutOutBlock(wallCutoutCount: 0);

        var cuttingElementIds = TfBlockDeserializer.ExtractCuttingElementIdsAndSteps(
            block, 0, dataSize, 363, out var steps);

        Assert.NotNull(cuttingElementIds);
        Assert.Empty(cuttingElementIds);
        Assert.Equal([(977, 2005)], steps!.Select(step => ((int)step.ClassId, step.ElementId)));
    }

    [Fact]
    public void GeomStepList_UndecodableStepListsNothing()
    {
        var (block, dataSize) = CreateCachedHostCutOutBlock(
            wallCutoutCount: 5, coincidentCurveCount: 0, coincidentOwner: 99);

        Assert.Null(TfBlockDeserializer.ExtractCuttingElementIdsAndSteps(
            block, 0, dataSize, 363, out var steps));
        Assert.Null(steps);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(uint.MaxValue)]
    [InlineData(1u)]
    public void GeomStepList_CoincidentFaceHistoryEndsPreserveCurveHistory(uint curveCount)
    {
        var (block, dataSize) = CreateCachedHostCutOutBlock(
            wallCutoutCount: 5, coincidentCurveCount: curveCount);

        var cuttingElementIds = TfBlockDeserializer.ExtractCuttingElementIds(
            block, 0, dataSize, 363, out string? diagnostic);

        Assert.True(cuttingElementIds != null, diagnostic);
        Assert.Equal([2001u, 2002u, 2003u, 2004u, 2006u], cuttingElementIds);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(uint.MaxValue)]
    [InlineData(1u)]
    public void GeomStepList_CoincidentFaceHistoryDoesNotAcceptWrongOwner(uint curveCount)
    {
        var (block, dataSize) = CreateCachedHostCutOutBlock(
            wallCutoutCount: 5, coincidentCurveCount: curveCount,
            coincidentOwner: 99);

        Assert.Null(TfBlockDeserializer.ExtractCuttingElementIds(
            block, 0, dataSize, 363));
    }

    [Fact]
    public void GridDatumPlaneFields_AreDecodedFromTheSchema()
    {
        var (block, dataSize) = CreateGridBlock(includePlane: false);

        var decoded = TfBlockDeserializer.ExtractParams(block, 0, dataSize, 0x083C);

        Assert.NotNull(decoded);
        Assert.Equal("A.1", decoded.StringFields["m_text"]);
        Assert.Equal(1.25, decoded.DoubleFields["m_freeEnd@0"]);
        Assert.Equal(2.5, decoded.DoubleFields["m_freeEnd@1"]);
        Assert.Equal(3.75, decoded.DoubleFields["m_freeEnd@2"]);
        Assert.Equal(11.25, decoded.DoubleFields["m_bubbleEnd@0"]);
        Assert.Equal(12.5, decoded.DoubleFields["m_bubbleEnd@1"]);
        Assert.Equal(3.75, decoded.DoubleFields["m_bubbleEnd@2"]);
        Assert.Equal(1, decoded.DoubleFields["m_cutVec@2"]);
    }

    [Fact]
    public void GridCurve_IsDecodedFromTheAuthoredPlaneEnvelope()
    {
        var (block, dataSize) = CreateGridBlock(includePlane: true);

        var datum = TfBlockDeserializer.ExtractGridDatum(block, 0, dataSize, 0x083C);

        Assert.NotNull(datum);
        Assert.Equal("A.1", datum.Name);
        Assert.Equal([14.0, 83.0, 3.0], datum.StartPoint);
        Assert.Equal([14.0, -206.0, 3.0], datum.EndPoint);
        Assert.Equal([0.0, 0.0, 1.0], datum.CutVector);
        // The vertical extent is the Z range of the plane envelope: origin Z 3 plus V in [-28, 534] (the plane's Y axis is Z).
        Assert.Equal([-25.0, 537.0], datum.LevelInterval);
    }

    [Fact]
    public void GridLevelInterval_IsTheVerticalEnvelopeOfThePlaneAroundItsOrigin()
    {
        var (block, dataSize) = CreateGridBlock(includePlane: true);

        var datum = TfBlockDeserializer.ExtractGridDatum(block, 0, dataSize, 0x083C);

        Assert.NotNull(datum);
        // Plane origin z = 3, Vmin = -28, Vmax = 534, the Y vector is (0, 0, 1).
        Assert.Equal([-25.0, 537.0], datum.LevelInterval);
    }

    [Fact]
    public void GridLevelInterval_IsAttachedToTheElementAndAbsentWithoutIt()
    {
        var state = new ParsingState();
        state.GridDatums[42] = new GridDatumData("A.1",
            [1.25, 2.5, 3.75], [11.25, 12.5, 3.75], [0, 0, 1], [-25, 537]);
        state.GridDatums[43] = new GridDatumData("A.2",
            [1.25, 2.5, 3.75], [11.25, 12.5, 3.75], [0, 0, 1]);
        var info = new RevitFileInfo();
        info.Elements.Add(new Element { Id = 42, TypeId = 1001, Name = "A.1" });
        info.Elements.Add(new Element { Id = 43, TypeId = 1001, Name = "A.2" });

        ElementBuilder.PostProcess(info, state);

        Assert.Equal([-25.0, 537.0], info.Elements.Single(e => e.Id == 42).SerializedGridLevelInterval);
        Assert.Null(info.Elements.Single(e => e.Id == 43).SerializedGridLevelInterval);
    }

    [Fact]
    public void LevelElevation_IsDecodedFromTheAuthoredPlaneOrigin()
    {
        var (block, dataSize) = CreateLevelBlock(47.25);

        double? elevation = TfBlockDeserializer.ExtractLevelElevation(
            block, 0, dataSize, 2299);

        Assert.Equal(47.25, elevation);
    }

    [Fact]
    public void TfWalkKeepsNativeParameterUpdatesInStreamOrder()
    {
        var (first, _) = CreateLevelBlock(10);
        var (another, _) = CreateLevelBlock(20);
        var (replacement, _) = CreateLevelBlock(30);
        BitConverter.GetBytes(78u).CopyTo(another, 0);
        var state = new ParsingState { Chunks = [[.. first, .. another, .. replacement]], AllKnownIds = [77, 78] };

        RvtFileReader.RunTfStreamWalk(state);

        Assert.Equal([77u, 78u], state.NativeParams.Keys.ToArray());
        Assert.Equal(30.0, state.NativeParams[77].DoubleFields["__levelElevation"]);
        Assert.Equal(20.0, state.NativeParams[78].DoubleFields["__levelElevation"]);
    }

    [Fact]
    public void SchemaDecodedGridDatum_IsAttachedAsTheAuthoritativeElementCurve()
    {
        var state = new ParsingState();
        state.GridDatums[42] = new GridDatumData("A.1",
            [1.25, 2.5, 3.75], [11.25, 12.5, 3.75], [0, 0, 1]);
        var info = new RevitFileInfo();
        info.Elements.Add(new Element { Id = 42, TypeId = 1001, Name = "A.1" });

        ElementBuilder.PostProcess(info, state);

        Element grid = Assert.Single(info.Elements);
        Assert.Equal(-2000220, grid.CategoryId);
        Assert.Equal("A.1", grid.Name);
        Parameter datumText = Assert.Single(grid.Parameters, parameter => parameter.Id == -1008000);
        Assert.Equal(StorageType.String, datumText.StorageType);
        Assert.Equal("A.1", datumText.Value);
        var curve = Assert.IsType<LocationCurve>(grid.Location);
        Assert.Equal([1.25, 2.5, 3.75], curve.StartPoint);
        Assert.Equal([11.25, 12.5, 3.75], curve.EndPoint);
        Assert.Equal(Math.Sqrt(200), curve.Length, 12);
        Assert.Null(grid.SerializedGridZInterval);   // no decoded surface, no interval
    }

    [Fact]
    public void GridDatumVerticalExtent_IsAttachedToTheGridElement()
    {
        var state = new ParsingState();
        state.GridDatums[42] = new GridDatumData("A.1", [1.25, 2.5, 3.75], [11.25, 12.5, 3.75], [0, 0, 1], [-25.0, 537.0]);
        var info = new RevitFileInfo();
        info.Elements.Add(new Element { Id = 42, TypeId = 1001, Name = "A.1" });

        ElementBuilder.PostProcess(info, state);

        Assert.Equal((-25.0, 537.0), Assert.Single(info.Elements).SerializedGridZInterval);
    }

    [Fact]
    public void GridDatumText_ReplacesTheGridTypeNameWithTheAuthoredMark()
    {
        var state = new ParsingState();
        state.GridDatums[42] = new GridDatumData("A.1",
            [1.25, 2.5, 3.75], [11.25, 12.5, 3.75], [0, 0, 1]);
        var info = new RevitFileInfo();
        info.Elements.Add(new Element { Id = 42, TypeId = 1001, Name = "8mm mark at end" });
        info.Elements.Add(new Element
        {
            Id = 1001,
            Name = "8mm mark at end",
            CategoryId = -2000220,
        });

        ElementBuilder.PostProcess(info, state);

        Element grid = Assert.Single(info.Elements, element => element.Id == 42);
        Assert.Equal(-2000220, grid.CategoryId);
        Assert.Equal("A.1", grid.Name);
        Assert.Equal("A.1", Assert.Single(grid.Parameters,
            parameter => parameter.Id == -1008000).Value);
        Assert.Equal([1.25, 2.5, 3.75], grid.Location!.StartPoint);
        Assert.Equal([11.25, 12.5, 3.75], grid.Location.EndPoint);
    }

    [Fact]
    public void GridDatumText_IsTheAuthoredMarkWhenNoOtherNameIsAvailable()
    {
        var state = new ParsingState();
        state.GridDatums[42] = new GridDatumData("A.1",
            [1.25, 2.5, 3.75], [11.25, 12.5, 3.75], [0, 0, 1]);
        var info = new RevitFileInfo();
        info.Elements.Add(new Element { Id = 42, TypeId = 1001 });

        ElementBuilder.PostProcess(info, state);

        Element grid = Assert.Single(info.Elements);
        Assert.Equal("A.1", grid.Name);
        Assert.Equal("A.1", Assert.Single(grid.Parameters,
            parameter => parameter.Id == -1008000).Value);
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        writer.Write((uint)value.Length);
        writer.Write(System.Text.Encoding.Unicode.GetBytes(value));
    }

    private static void WriteVector(BinaryWriter writer, double x, double y, double z)
    {
        writer.Write(x);
        writer.Write(y);
        writer.Write(z);
    }

    private static (byte[] Block, uint DataSize) CreateGridBlock(bool includePlane)
    {
        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload, System.Text.Encoding.Unicode, leaveOpen: true);

        // Element base fields. All owned/polymorphic pointers and the dynamic
        // constraint-info container are null/empty in this minimal valid block.
        for (int i = 0; i < 6; i++) writer.Write(0u);
        writer.Write(0u); // m_constrInfo count
        writer.Write(0u); // m_cellList
        writer.Write(0u); // m_docAccess
        writer.Write(42); // m_id
        for (int i = 0; i < 7; i++) writer.Write(0); // ElementId fields
        writer.Write((byte)0); // m_locked
        writer.Write((byte)0); // m_moribund
        writer.Write((byte)0); // m_dummy

        WriteString(writer, "A.1");
        writer.Write(0u); // m_pFace
        writer.Write(includePlane ? 1u : 0u); // m_pSurface
        if (includePlane) writer.Write((ushort)522); // Plane class tag
        writer.Write(0u); // m_pLeader
        WriteVector(writer, 1.25, 2.5, 3.75);       // m_freeEnd
        WriteVector(writer, 11.25, 12.5, 3.75);     // m_bubbleEnd
        WriteVector(writer, 0, 0, 1);               // m_cutVec
        for (int i = 0; i < 6; i++) writer.Write(0.0); // two XYZ datum ref-points
        writer.Write(0.0); // m_sheetTextHeight
        writer.Write(0);   // m_genDbViewId
        writer.Write((byte)0); // m_v2Datum
        writer.Write((byte)0); // m_useConstVForDatumLine3dRep
        writer.Write(0u); // m_pGridChainMembershipCell
        writer.Write(1001); // m_attrId
        writer.Write((byte)1); // m_showCenterMark

        if (includePlane)
        {
            writer.Write(-195.0); // Umin
            writer.Write(-28.0);  // Vmin
            writer.Write(94.0);   // Umax
            writer.Write(534.0);  // Vmax
            writer.Write((byte)1); // Surface.m_orientFlag
            WriteVector(writer, 14, -112, 3); // Plane.m_origin
            WriteVector(writer, 0, -1, 0);    // Plane.m_xVec
            WriteVector(writer, 0, 0, 1);     // Plane.m_yVec
        }

        byte[] fields = payload.ToArray();
        uint dataSize = checked((uint)fields.Length + 2);
        byte[] block = new byte[fields.Length + 18];
        BitConverter.GetBytes(42u).CopyTo(block, 0);
        BitConverter.GetBytes(dataSize).CopyTo(block, 8);
        BitConverter.GetBytes((ushort)0x083C).CopyTo(block, 12);
        fields.CopyTo(block, 14);
        BitConverter.GetBytes(dataSize).CopyTo(block, block.Length - 4);
        return (block, dataSize);
    }

    private static (byte[] Block, uint DataSize) CreateHostCutOutBlock()
    {
        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload, System.Text.Encoding.Unicode,
            leaveOpen: true);

        for (int i = 0; i < 4; i++) writer.Write(0u); // parameter stores
        writer.Write(1u);                            // m_geomSteps
        writer.Write((ushort)2086);                  // GeomStepList
        writer.Write(0u);                            // m_pGeomTable
        writer.Write(0u);                            // m_constrInfo
        writer.Write(0u);                            // m_cellList
        writer.Write(0u);                            // m_docAccess
        writer.Write(42);                            // m_id
        for (int i = 0; i < 7; i++) writer.Write(0); // ElementId fields
        writer.Write((byte)0);                       // m_locked
        writer.Write((byte)0);                       // m_moribund
        writer.Write((byte)0);                       // m_dummy
        writer.Write(0u);                            // m_hostObjMiscData
        writer.Write(0u);                            // m_oGeomToElemMap
        writer.Write((byte)1);                       // m_roomBounding

        // Deferred GeomStepList. Only the two cut phases contain steps.
        writer.Write(0u); // m_nonBRepGList
        writer.Write(0u); // m_bRepFormGList
        writer.Write(0u); // m_bRepAdjustGList
        WriteGeomStepPointers(writer, 2); // m_bRepCutOutGList
        WriteGeomStepPointers(writer, 1); // m_bRepPostCutOutGList
        writer.Write(0u); // m_bRepTweakGList
        writer.Write(0u); // m_bRepFormSnapshot
        writer.Write(0u); // m_bRepAdjustSnapshot
        writer.Write(0u); // m_bRepCutOutSnapshot
        writer.Write(0u); // m_bRepPostCutOutSnapshot
        writer.Write(42u); // m_pElem
        for (int i = 0; i < 5; i++) writer.Write(0);
        writer.Write(3); // m_idCounter
        writer.Write((byte)0); // m_flags

        WriteGeomStep(writer, 1001u);
        WriteGeomStep(writer, 1002u);
        WriteGeomStep(writer, 1003u);

        byte[] fields = payload.ToArray();
        uint dataSize = checked((uint)fields.Length + 2);
        byte[] block = new byte[fields.Length + 18];
        BitConverter.GetBytes(42u).CopyTo(block, 0);
        BitConverter.GetBytes(dataSize).CopyTo(block, 8);
        BitConverter.GetBytes((ushort)363).CopyTo(block, 12);
        fields.CopyTo(block, 14);
        BitConverter.GetBytes(dataSize).CopyTo(block, block.Length - 4);
        return (block, dataSize);
    }

    private static void WriteGeomStepPointers(BinaryWriter writer, int count)
    {
        writer.Write((uint)count);
        for (int index = 0; index < count; index++)
        {
            writer.Write(1u);
            writer.Write((ushort)4137);
        }
    }

    private static void WriteGeomStep(BinaryWriter writer, uint ownerElementId)
    {
        writer.Write(0u); // m_faceHistTable
        writer.Write(0u); // m_curveHistTableSet
        writer.Write(0u); // m_edgeHistTable
        writer.Write(0u); // m_edgeHistTableReverse
        writer.Write(0);  // GeomGenerator.m_id
        writer.Write(15); // GeomStep.m_version
        writer.Write(0);  // GeomStep.m_flags
        writer.Write(2u); // GeomStep.m_pElem back-reference to the host
        writer.Write(1);  // WallCutoutGStepBase.m_bCutSomething
        writer.Write((int)ownerElementId); // m_instId
        writer.Write(-1); // m_infillIdBefore
        writer.Write(-1); // m_infillIdAfter
        writer.Write((byte)1); // m_bIsTrueBoolean
        writer.Write((byte)0); // m_bUseUnattachedVoids
        writer.Write((byte)0); // m_removedWholeGeometry
        writer.Write(0u); // m_bIsTrueBooleanArr
    }

    private static (byte[] Block, uint DataSize) CreateCachedHostCutOutBlock(
        int wallCutoutCount = 4, uint coincidentCurveCount = 0,
        uint coincidentOwner = 2)
    {
        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload, System.Text.Encoding.Unicode,
            leaveOpen: true);

        for (int i = 0; i < 4; i++) writer.Write(0u); // parameter stores
        writer.Write(1u);                            // m_geomSteps
        writer.Write((ushort)2086);                  // GeomStepList
        writer.Write(1u);                            // m_pGeomTable
        writer.Write((ushort)2256);                  // JoinEndGStep
        writer.Write(0u);                            // m_constrInfo
        writer.Write(0u);                            // m_cellList
        writer.Write(0u);                            // m_docAccess
        writer.Write(42);                            // m_id
        for (int i = 0; i < 7; i++) writer.Write(0); // ElementId fields
        writer.Write((byte)0);                       // m_locked
        writer.Write((byte)0);                       // m_moribund
        writer.Write((byte)0);                       // m_dummy
        writer.Write(0u);                            // m_hostObjMiscData
        writer.Write(0u);                            // m_oGeomToElemMap
        writer.Write((byte)1);                       // m_roomBounding

        writer.Write(0u); // m_nonBRepGList
        writer.Write(1u); // m_bRepFormGList
        writer.Write(1u);
        writer.Write((ushort)2256); // cached JoinEndGStep
        writer.Write(0u); // m_bRepAdjustGList
        writer.Write((uint)(wallCutoutCount + 1)); // m_bRepCutOutGList
        for (int index = 0; index < wallCutoutCount; index++)
        {
            writer.Write(1u);
            writer.Write((ushort)4137);
        }
        writer.Write(1u);
        writer.Write((ushort)977); // CutPairGStep
        writer.Write(0u); // m_bRepPostCutOutGList
        writer.Write(0u); // m_bRepTweakGList
        writer.Write(0u); // m_bRepFormSnapshot
        writer.Write(0u); // m_bRepAdjustSnapshot
        writer.Write(0u); // m_bRepCutOutSnapshot
        writer.Write(0u); // m_bRepPostCutOutSnapshot
        writer.Write(42u); // m_pElem
        for (int i = 0; i < 5; i++) writer.Write(0);
        writer.Write(3); // m_idCounter
        writer.Write((byte)0); // m_flags

        WriteJoinEndWithReverseHistory(writer);
        WriteJoinEndWithCompactHistory(writer);
        if (wallCutoutCount > 0) WritePrefixedTerminatedGeomStep(writer, 2001);
        if (wallCutoutCount > 1) WriteUnprefixedTerminatedGeomStep(writer, 2002);
        if (wallCutoutCount > 2) WriteCountedGeomStep(writer, 2003);
        if (wallCutoutCount > 3) WriteCompactCountedGeomStep(writer, 2004);
        if (wallCutoutCount > 4)
            WriteCoincidentFaceHistoryGeomStep(writer, 2006,
                coincidentCurveCount, coincidentOwner);
        WriteCutPairGeomStep(writer, 2005);

        byte[] fields = payload.ToArray();
        uint dataSize = checked((uint)fields.Length + 2);
        byte[] block = new byte[fields.Length + 18];
        BitConverter.GetBytes(42u).CopyTo(block, 0);
        BitConverter.GetBytes(dataSize).CopyTo(block, 8);
        BitConverter.GetBytes((ushort)363).CopyTo(block, 12);
        fields.CopyTo(block, 14);
        BitConverter.GetBytes(dataSize).CopyTo(block, block.Length - 4);
        return (block, dataSize);
    }

    private static void WriteJoinEndWithReverseHistory(BinaryWriter writer)
    {
        writer.Write(0u); // m_faceHistTable
        writer.Write(0u); // m_curveHistTableSet
        writer.Write(uint.MaxValue); // m_edgeHistTable
        writer.Write(1u); // reverse-history high-water id
        writer.Write(0u); // reserved
        writer.Write(1u); // entry count
        for (int i = 1; i <= 5; i++) writer.Write(i); // EdgeHistEntry
        writer.Write(7);  // GeomGenerator.m_id
        writer.Write(15); // GeomStep.m_version
        writer.Write(0);  // GeomStep.m_flags
        writer.Write(2u); // GeomStep.m_pElem
        writer.Write(0);  // m_end
        writer.Write(0);  // m_weakIdx
        writer.Write((byte)0); // m_hasRealJoined
        writer.Write(0u); // m_geomJoined
        writer.Write(0u); // m_realJoined
        writer.Write(0u); // m_exposedFaceMap
    }

    private static void WriteJoinEndWithCompactHistory(BinaryWriter writer)
    {
        writer.Write(0u); // empty face history
        writer.Write(0u); // empty curve history
        writer.Write(2u); // compact EdgeHistEntry count
        writer.Write(31); writer.Write(1); writer.Write(0); writer.Write(-1);
        writer.Write(32); writer.Write(2); writer.Write(0); writer.Write(-1);
        writer.Write(0u); // compact edge-history terminator
        writer.Write(2u); // reverse EdgeHistEntry count
        writer.Write(31); writer.Write(1); writer.Write(0); writer.Write(0); writer.Write(0);
        writer.Write(32); writer.Write(2); writer.Write(0); writer.Write(1); writer.Write(0);
        writer.Write(0u); // reverse edge-history terminator
        writer.Write(9);  // GeomGenerator.m_id
        writer.Write(15); // GeomStep.m_version
        writer.Write(0);  // GeomStep.m_flags
        writer.Write(2u); // GeomStep.m_pElem
        writer.Write(0);  // m_end
        writer.Write(0);  // m_weakIdx
        writer.Write((byte)0); // m_hasRealJoined
        writer.Write(0u); // m_geomJoined
        writer.Write(0u); // m_realJoined
        writer.Write(0u); // m_exposedFaceMap
    }

    private static void WritePrefixedTerminatedGeomStep(BinaryWriter writer, int cutterId)
    {
        writer.Write(120); // persisted high-water id
        writer.Write(5); writer.Write(0); writer.Write(-1); writer.Write(125);
        writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0); // terminator
        writer.Write(0u); // curve history count (20-byte legacy records)
        writer.Write(0u); // empty edge history; reverse table is omitted
        WriteWallCutTail(writer, cutterId);
    }

    private static void WriteUnprefixedTerminatedGeomStep(BinaryWriter writer, int cutterId)
    {
        writer.Write(1); writer.Write(0); writer.Write(-1); writer.Write(155);
        writer.Write(2); writer.Write(0); writer.Write(-1); writer.Write(68);
        writer.Write(3); writer.Write(0); writer.Write(0); writer.Write(64);
        writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0); // terminator
        writer.Write(1u); // curve history count
        writer.Write(69); writer.Write(1); writer.Write(0); writer.Write(0); writer.Write(0);
        writer.Write(66); // edge-history high-water id
        writer.Write(2); writer.Write(3); writer.Write(2); writer.Write(0); writer.Write(0);
        writer.Write(uint.MaxValue); // empty reverse edge history
        WriteWallCutTail(writer, cutterId);
    }

    private static void WriteCountedGeomStep(BinaryWriter writer, int cutterId)
    {
        writer.Write(2u); // FaceHistEntry count
        writer.Write(0); writer.Write(0); writer.Write(64); writer.Write(3);
        writer.Write(1); writer.Write(0); writer.Write(64); writer.Write(3);
        writer.Write(2u); // CurveHistEntry count (24-byte records)
        writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(12); writer.Write(69); writer.Write(1);
        writer.Write(1); writer.Write(0); writer.Write(0); writer.Write(70); writer.Write(1); writer.Write(0);
        writer.Write(2u); // EdgeHistEntry count
        writer.Write(10); writer.Write(1); writer.Write(0); writer.Write(0); writer.Write(0);
        writer.Write(11); writer.Write(1); writer.Write(0); writer.Write(1); writer.Write(0);
        writer.Write(0u); // empty reverse edge history
        WriteWallCutTail(writer, cutterId);
    }

    private static void WriteCompactCountedGeomStep(BinaryWriter writer, int cutterId)
    {
        writer.Write(0u); // empty face history
        writer.Write(0u); // empty curve history
        writer.Write(2u); // compact EdgeHistEntry count
        writer.Write(21); writer.Write(1); writer.Write(0); writer.Write(-1);
        writer.Write(22); writer.Write(2); writer.Write(0); writer.Write(-1);
        writer.Write(0u); // compact edge-history terminator
        writer.Write(2u); // reverse EdgeHistEntry count
        writer.Write(21); writer.Write(1); writer.Write(0); writer.Write(0); writer.Write(0);
        writer.Write(22); writer.Write(2); writer.Write(0); writer.Write(1); writer.Write(0);
        writer.Write(0u); // reverse edge-history terminator
        WriteWallCutTail(writer, cutterId);
    }

    private static void WriteCoincidentFaceHistoryGeomStep(BinaryWriter writer,
        int cutterId, uint curveCount, uint owner)
    {
        writer.Write(1u); // count or persisted high-water id
        writer.Write(30); writer.Write(0); writer.Write(0); writer.Write(0);
        // The one counted record and prefixed terminator have the same end.
        writer.Write(curveCount);
        if (curveCount == 1)
        {
            // A nonempty counted curve table still requires its 24-byte records.
            writer.Write(1); writer.Write(0); writer.Write(0);
            writer.Write(0); writer.Write(0); writer.Write(0);
        }
        writer.Write(0u); // empty edge history
        writer.Write(0u); // empty reverse edge history
        WriteWallCutTail(writer, cutterId, owner);
    }

    private static void WriteWallCutTail(BinaryWriter writer, int cutterId,
        uint owner = 2)
    {
        writer.Write(8);  // GeomGenerator.m_id
        writer.Write(15); // GeomStep.m_version
        writer.Write(0);  // GeomStep.m_flags
        writer.Write(owner); // GeomStep.m_pElem back-reference to the host
        writer.Write(1);  // WallCutoutGStepBase.m_bCutSomething
        writer.Write(cutterId); // m_instId
        writer.Write(-1); // m_infillIdBefore
        writer.Write(-1); // m_infillIdAfter
        writer.Write((byte)1); // m_bIsTrueBoolean
        writer.Write((byte)0); // m_bUseUnattachedVoids
        writer.Write((byte)0); // m_removedWholeGeometry
        writer.Write(0u); // m_bIsTrueBooleanArr
    }

    private static void WriteCutPairGeomStep(BinaryWriter writer, int otherElementId)
    {
        writer.Write(0u); // m_faceHistTable
        writer.Write(0u); // m_curveHistTableSet
        writer.Write(0u); // m_edgeHistTable; reverse history is omitted
        writer.Write(9);  // GeomGenerator.m_id
        writer.Write(15); // GeomStep.m_version
        writer.Write(0);  // GeomStep.m_flags
        writer.Write(2u); // GeomStep.m_pElem back-reference to the host
        writer.Write(otherElementId); // PairBaseGStep.m_otherId
        writer.Write((byte)0); // m_userSet
        writer.Write((byte)0); // m_allowUserUnjoin
        writer.Write((byte)0); // m_isAssociatedWithColJoinStep
        writer.Write(0u); // m_mergedFaceInfo
        writer.Write((byte)0); // m_NoWarningCheck
        writer.Write((byte)0); // m_bOutlinesOverlapWithoutInt
        writer.Write((byte)0); // m_allDetailLevels
        writer.Write((byte)0); // m_offsetCutter
        writer.Write((byte)0); // m_isTop
    }

    private static (byte[] Block, uint DataSize) CreateLevelBlock(double elevation)
    {
        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload, System.Text.Encoding.Unicode, leaveOpen: true);

        for (int i = 0; i < 6; i++) writer.Write(0u);
        writer.Write(0u); // m_constrInfo count
        writer.Write(0u); // m_cellList
        writer.Write(0u); // m_docAccess
        writer.Write(77); // m_id
        writer.Write(0);  // m_assocLevelId
        writer.Write(-1); // m_famId
        writer.Write(-1); // m_unplacedOwnerId
        writer.Write(-1); // m_ownerDBViewId
        writer.Write(0);  // m_createdPhaseId
        writer.Write(0);  // m_demolishedPhaseId
        writer.Write(0);  // m_designOptionId
        writer.Write((byte)0); // m_locked
        writer.Write((byte)0); // m_moribund
        writer.Write((byte)0); // m_dummy

        WriteString(writer, "Level 1");
        writer.Write(0u); // m_pFace
        writer.Write(1u); // m_pSurface
        writer.Write((ushort)522); // Plane class tag
        writer.Write(0u); // m_pLeader
        WriteVector(writer, 0, 0, elevation);
        WriteVector(writer, 10, 0, elevation);
        WriteVector(writer, 0, 0, 1);
        for (int i = 0; i < 6; i++) writer.Write(0.0);
        writer.Write(0.0); // m_sheetTextHeight
        writer.Write(0);   // m_genDbViewId
        writer.Write((byte)0); // m_v2Datum
        writer.Write((byte)0); // m_useConstVForDatumLine3dRep
        writer.Write(0.0); // m_roomComputationElevationOffset
        writer.Write(1001); // m_attrId
        writer.Write((byte)1); // m_isBuildingStory

        writer.Write(-100.0); // Umin
        writer.Write(-100.0); // Vmin
        writer.Write(100.0);  // Umax
        writer.Write(100.0);  // Vmax
        writer.Write((byte)1); // Surface.m_orientFlag
        WriteVector(writer, 0, 0, elevation); // Plane.m_origin
        WriteVector(writer, 1, 0, 0);         // Plane.m_xVec
        WriteVector(writer, 0, 1, 0);         // Plane.m_yVec

        byte[] fields = payload.ToArray();
        uint dataSize = checked((uint)fields.Length + 2);
        byte[] block = new byte[fields.Length + 18];
        BitConverter.GetBytes(77u).CopyTo(block, 0);
        BitConverter.GetBytes(dataSize).CopyTo(block, 8);
        BitConverter.GetBytes((ushort)2299).CopyTo(block, 12);
        fields.CopyTo(block, 14);
        BitConverter.GetBytes(dataSize).CopyTo(block, block.Length - 4);
        return (block, dataSize);
    }
}
