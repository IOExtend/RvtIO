using System.Text;
using RvtIO.Model;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

/// <summary>
/// The readings <see cref="WideNativeParams"/> takes from the walk of a block with 64-bit ids (Level elevation, project phase order, room
/// phase, system discriminator, the stored demolished phase, the room calculation point) and the capture of a whole counted array (<see cref="WideBlockWalker.AllItemsKeyPrefix"/>),
/// on hand-made blocks of a schema that has the class and field names of the Revit 2025 files; then the phase pass that consumes them.
/// </summary>
public class WideNativeParamsTests
{
    private const int IdentifierId = 1, ElementIdId = 2, PlaneId = 3, LevelId = 4, PhasesId = 5, RoomId = 6, RootId = 7, SubRootId = 8, HolderId = 9,
        NamedParamId = 10, FamilyParamsId = 11, InlineParamsId = 12, QueuedParamsId = 13, XyzId = 14, CalculationPointId = 15, RegenHolderId = 16,
        PlainPointId = 17, PlanePointId = 18, BaseRailingAttrId = 19, StairsRailingAttrId = 20, OtherAttrId = 21;

    private static SchemaField Field(string name, byte type, byte mods = 0, int? reference = null, int? fixedCount = null) =>
        new(name, type, mods, 0, fixedCount, reference, null, null);

    private static SchemaDocument Schema(int xyzSize = 3) => new()
    {
        Classes =
        [
            new(IdentifierId, "Identifier", 0, 2, [Field("m_id64", 11)], []),
            new(ElementIdId, "ElementId", 0, 1, [Field("m_id", 14, 0, IdentifierId)], []),
            new(PlaneId, "Plane", 0, 1, [Field("m_origin", 7, 16, null, 3)], []),
            new(LevelId, "Level", 0, 1, [Field("m_plane", 14, 0, PlaneId)], []),
            new(PhasesId, "AllProjectPhases", 0, 1, [Field("m_phaseIds", 14, 80, ElementIdId)], []),
            new(RoomId, "RoomElem", 0, 1, [Field("m_phaseId", 14, 0, ElementIdId)], []),
            new(RootId, "SystemRoot", 0, 1, [Field("m_footingType", 4)], []),
            new(SubRootId, "SystemSubRoot", RootId, 1, [Field("m_constructionMethod", 4)], []),
            new(HolderId, "Holder", 0, 1, [Field("m_createdPhaseId", 14, 0, ElementIdId), Field("m_demolishedPhaseId", 14, 0, ElementIdId)], []),
            new(NamedParamId, "NamedParam", 0, 1, [Field("m_value", 7), Field("m_paramId", 14, 0, ElementIdId)], []),
            new(FamilyParamsId, "FamilyParams", 0, 1, [Field("m_params", 14, 80, NamedParamId)], []),
            new(InlineParamsId, "InlineParams", 0, 1, [Field("m_subInstParams", 14, 0, FamilyParamsId)], []),
            new(QueuedParamsId, "QueuedParams", 0, 1, [Field("m_pParams", 14, 2)], []),
            new(XyzId, "XYZ", 0, 1, [Field("m_xyz", 7, 16, null, xyzSize)], []),
            new(CalculationPointId, "SpatialElementCalculationPointRegenData", 0, 1, [Field("m_position", 14, 0, XyzId)], []),
            new(RegenHolderId, "RegenHolder", 0, 1, [Field("m_arrFamSymRegenData", 14, 81)], []),
            new(PlainPointId, "PlainPoint", 0, 1, [Field("m_position", 14, 0, XyzId)], []),
            new(PlanePointId, "PlanePoint", 0, 1, [Field("m_position", 14, 0, PlaneId)], []),
            new(BaseRailingAttrId, "BaseRailingAttr", 0, 1, [Field("m_topRailHeight", 7)], []),
            new(StairsRailingAttrId, "StairsRailingAttr", BaseRailingAttrId, 1, [], []),
            new(OtherAttrId, "OtherAttr", 0, 1, [Field("m_topRailHeight", 7)], []),
        ],
    };

    private static readonly WideNativeParams Params = new(Schema());

    [Theory]
    [InlineData("Symbol", "Symbol")]
    [InlineData("", "Family")]
    [InlineData("SecretInternalStyle", "Family")]
    public void OwnName_PrefersTheHighestRankedFieldAndOnlyItsFirstText(string symbol, string expected)
    {
        var walk = new WideBlockWalker.Walk();
        void Name(string declaring, string text) => walk.Captures.Add(new("name", WideBlockWalker.CaptureKind.Text,
            0, 0, text, false, 0, declaring, "m_name"));
        Name("Material", "Material");
        Name("SymbolInfo", symbol);
        Name("SymbolInfo", "Second symbol");
        Name("FamilyBase", "Family");
        Assert.Equal(expected, Params.OwnName(walk, RootId));
    }

    [Fact]
    public void OwnName_UsesAParameterCaptionOnlyForAParameterElement()
    {
        var schema = Schema();
        schema.Classes.Add(new(22, "ParamElem", 0, 1, [], []));
        var reader = new WideNativeParams(schema);
        var walk = new WideBlockWalker.Walk();
        walk.Captures.Add(new("caption", WideBlockWalker.CaptureKind.Text, 0, 0, "Caption", false, 0, "ParamDef", "m_caption"));
        Assert.Null(reader.OwnName(walk, RootId));
        Assert.Equal("Caption", reader.OwnName(walk, 22));
    }

    private static byte[] Block(int classId, Action<BinaryWriter> body)
    {
        var stream = new MemoryStream();
        var w = new BinaryWriter(stream, Encoding.Unicode);
        w.Write((ushort)classId);
        body(w);
        return stream.ToArray();
    }

    private static WideBlockWalker.Walk Walk(int classId, Action<BinaryWriter> body)
    {
        var block = Block(classId, body);
        var walk = Params.Walker.Run(block, 0, block.Length, classId);
        Assert.True(walk.Exact, walk.Error);
        return walk;
    }

    [Fact]
    public void LevelElevation_IsTheZOfThePlaneOrigin_AndOnlyOfALevel()
    {
        var walk = Walk(LevelId, w => { w.Write(1.5); w.Write(2.5); w.Write(10.25); });
        Assert.Equal(10.25, Params.LevelElevation(walk, LevelId));
        Assert.Null(Params.LevelElevation(walk, RoomId));
    }

    [Fact]
    public void LevelElevation_IsNullForANonFiniteZ()
    {
        var walk = Walk(LevelId, w => { w.Write(0.0); w.Write(0.0); w.Write(double.NaN); });
        Assert.Null(Params.LevelElevation(walk, LevelId));
    }

    [Fact]
    public void ProjectPhaseOrder_KeepsEveryIdInTheAuthoredOrder()
    {
        // not sorted by value: the order of the array is the order of the phases
        var walk = Walk(PhasesId, w => { w.Write(3u); w.Write(50L); w.Write(0L); w.Write(7L); });
        Assert.Equal([50, 0, 7], Params.ProjectPhaseOrder(walk, PhasesId));
        Assert.Null(Params.ProjectPhaseOrder(walk, LevelId));
    }

    [Fact]
    public void ProjectPhaseOrder_IsNullForAnEmptyArray_AndDropsIdsOutsideTheIntRange()
    {
        Assert.Null(Params.ProjectPhaseOrder(Walk(PhasesId, w => w.Write(0u)), PhasesId));
        var walk = Walk(PhasesId, w => { w.Write(3u); w.Write(4L); w.Write(-1L); w.Write(1L << 40); });
        Assert.Equal([4], Params.ProjectPhaseOrder(walk, PhasesId));
    }

    [Fact]
    public void RoomPhase_IsTheStoredId_AndNullForNoPhaseOrAnotherClass()
    {
        Assert.Equal(86961, Params.RoomPhase(Walk(RoomId, w => w.Write(86961L)), RoomId));
        Assert.Equal(0, Params.RoomPhase(Walk(RoomId, w => w.Write(0L)), RoomId));
        Assert.Null(Params.RoomPhase(Walk(RoomId, w => w.Write(-1L)), RoomId));
        Assert.Null(Params.RoomPhase(Walk(LevelId, w => { w.Write(0.0); w.Write(0.0); w.Write(0.0); }), LevelId));
    }

    [Fact]
    public void SystemDiscriminator_IsTheValueOfTheFieldTheRootClassItselfDeclares()
    {
        Assert.Equal(3, Params.SystemDiscriminator(Walk(RootId, w => w.Write(3)), RootId));
        // a derived root reads its own field (m_constructionMethod), not the one of its base class
        Assert.Equal(9, Params.SystemDiscriminator(Walk(SubRootId, w => { w.Write(4); w.Write(9); }), SubRootId));
        // a class without a field of the set has no discriminator
        Assert.Null(Params.SystemDiscriminator(Walk(RoomId, w => w.Write(5L)), RoomId));
    }

    [Fact]
    public void Build_KeepsADemolishedPhaseOfMinusOneOfA64BitId()
    {
        var np = Params.Build(Walk(HolderId, w => { w.Write(86961L); w.Write(-1L); }));
        Assert.NotNull(np);
        Assert.Equal(86961, np.Fields["m_createdPhaseId"]);
        Assert.Equal(-1, np.Fields["m_demolishedPhaseId"]);
    }

    [Fact]
    public void Build_KeepsTheRoomCalculationPointOfAQueuedRegenDataObjectAsThreeDoubles()
    {
        // one owned pointer in the counted array (handle, class tag), then the queued object: the XYZ of its m_position
        var np = Params.Build(Walk(RegenHolderId, w =>
        {
            w.Write(1u); w.Write(7u); w.Write((ushort)CalculationPointId);
            w.Write(-0.5); w.Write(0.25); w.Write(5.375);
        }));
        Assert.NotNull(np);
        Assert.Equal(-0.5, np.DoubleFields["3639:m_position@0"]);
        Assert.Equal(0.25, np.DoubleFields["3639:m_position@1"]);
        Assert.Equal(5.375, np.DoubleFields["3639:m_position@2"]);
    }

    [Fact]
    public void Build_KeepsTheTopRailHeightOfARailingTypeBare_AndOnlyFromBaseRailingAttr()
    {
        // A railing type of a file read by its own schema (Revit 2020, 2024, ...): StairsRailingAttr inherits
        // BaseRailingAttr.m_topRailHeight, which ElementBuilder reads as DoubleFields["m_topRailHeight"].
        var np = Params.Build(Walk(StairsRailingAttrId, w => w.Write(4.0)));
        Assert.NotNull(np);
        Assert.Equal(4.0, np.DoubleFields["m_topRailHeight"]);

        var other = Params.Build(Walk(OtherAttrId, w => w.Write(4.0)));
        Assert.False(other?.DoubleFields.ContainsKey("m_topRailHeight") ?? false);
    }

    [Fact]
    public void Walker_CapturesAStructFieldAsThreeDoublesOnlyWhenItIsKeyedAndAnXyzOfThreeDoubles()
    {
        static string? Key(SchemaClass declaring, SchemaField field) => field.Name == "m_position" ? "point" : null;
        var point = Block(PlainPointId, w => { w.Write(1.0); w.Write(2.0); w.Write(3.0); });
        var captures = new WideBlockWalker(Schema(), Key, []).Run(point, 0, point.Length, PlainPointId).Captures;
        Assert.Equal(["point@0", "point@1", "point@2"], captures.Select(c => c.Key));
        Assert.Equal([1.0, 2.0, 3.0], captures.Select(c => c.Real));

        // not keyed
        Assert.Empty(new WideBlockWalker(Schema(), (_, _) => null, []).Run(point, 0, point.Length, PlainPointId).Captures);
        // a struct of three doubles that is no XYZ
        var plane = Block(PlanePointId, w => { w.Write(1.0); w.Write(2.0); w.Write(3.0); });
        Assert.Empty(new WideBlockWalker(Schema(), Key, []).Run(plane, 0, plane.Length, PlanePointId).Captures);
        // an XYZ of another size
        var flat = Block(PlainPointId, w => { w.Write(1.0); w.Write(2.0); });
        Assert.Empty(new WideBlockWalker(Schema(xyzSize: 2), Key, []).Run(flat, 0, flat.Length, PlainPointId).Captures);
    }

    [Fact]
    public void Build_KeepsTheNamedParamsOfTheQueuedFamilyParams_NotThoseReadInlineFromARootField()
    {
        // FamilySymbol.m_subInstData holds the parameters of the components nested in the family: not parameters of the symbol
        var inline = Params.Build(Walk(InlineParamsId, w => { w.Write(1u); w.Write(2.5); w.Write(764903L); }));
        Assert.NotNull(inline);
        Assert.Empty(inline.NamedParams);
        var queued = Params.Build(Walk(QueuedParamsId, w =>
        {
            w.Write(1u); w.Write((ushort)FamilyParamsId);
            w.Write(1u); w.Write(3.5); w.Write(764903L);
        }));
        Assert.NotNull(queued);
        var record = Assert.Single(queued.NamedParams);
        Assert.Equal(764903, record.ParamId);
        Assert.Equal(3.5, record.Value);
    }

    [Fact]
    public void AllItemsKey_CapturesEveryItemWhereOtherKeysCaptureTheFirstOnly()
    {
        var all = new WideBlockWalker(Schema(), (declaring, field) =>
            declaring.Name == "AllProjectPhases" ? WideBlockWalker.AllItemsKeyPrefix + "phases" : null, []);
        var first = new WideBlockWalker(Schema(), (declaring, field) => declaring.Name == "AllProjectPhases" ? "first" : null, []);
        var block = Block(PhasesId, w => { w.Write(3u); w.Write(10L); w.Write(20L); w.Write(30L); });

        var allCaptures = all.Run(block, 0, block.Length, PhasesId).Captures;
        Assert.Equal(["all:phases#", "all:phases@0", "all:phases@1", "all:phases@2"], allCaptures.Select(c => c.Key));
        Assert.Equal([3L, 10, 20, 30], allCaptures.Select(c => c.Integer));

        var firstCaptures = first.Run(block, 0, block.Length, PhasesId).Captures;
        Assert.Equal(["first#", "first@0"], firstCaptures.Select(c => c.Key));
    }
}

/// <summary>Which stores of a walk are the element's own (<see cref="WideNativeParams.Build"/>): the object the root queues for its parameter
/// values, and a store queued by a nested object, which a material has (the ParamSet of its physical properties).</summary>
public class WideParameterStoresTests
{
    private const int StoresId = 1, SetId = 2, MaterialId = 3, PropertySetId = 4;

    private static SchemaField Field(string name, byte type, byte mods = 0) => new(name, type, mods, 0, null, null, null, null);

    private static readonly WideNativeParams Params = new(new SchemaDocument
    {
        Classes =
        [
            new(StoresId, "ParamValueSetDouble", 0, 1, [Field("m_n", 4)], []),
            new(SetId, "ParamSet", 0, 1, [Field("m_pStores", 14, 1)], []),
            new(MaterialId, "MaterialElem", 0, 1, [Field("m_pParamValueSetDouble", 14, 1), Field("m_pSet", 14, 1)], []),
            new(PropertySetId, "PropertySetElement", 0, 1, [Field("m_pParamValueSetDouble", 14, 1), Field("m_pSet", 14, 1)], []),
        ],
    });

    /// <summary>[class][stores pointer][set pointer], then the stores object, the set object and the store the set queued.</summary>
    private static WideBlockWalker.Walk Walk(int rootClass)
    {
        var stream = new MemoryStream();
        var w = new BinaryWriter(stream, Encoding.Unicode);
        w.Write((ushort)rootClass);
        w.Write(1u); w.Write((ushort)StoresId);
        w.Write(2u); w.Write((ushort)SetId);
        w.Write(7);                                   // the stores object
        w.Write(3u); w.Write((ushort)StoresId);       // the set object queues a store of its own
        w.Write(8);                                   // the nested store
        var block = stream.ToArray();
        var walk = Params.Walker.Run(block, 0, block.Length, rootClass);
        Assert.True(walk.Exact, walk.Error);
        Assert.Equal(3, walk.Queue.Count);
        return walk;
    }

    [Fact]
    public void AStoreQueuedByANestedObjectOfAMaterial_DoesNotMakeItsStoresIncomplete()
        => Assert.True(Params.Build(Walk(MaterialId), MaterialId)!.RootParameterStoresComplete);

    [Theory]
    [InlineData(PropertySetId)]   // its Class and Property Set Name are values of nested objects
    [InlineData(-1)]              // the class of the root is not given
    public void AStoreQueuedByANestedObjectOfAnotherClass_MakesItsStoresIncomplete(int rootClass)
        => Assert.False(Params.Build(Walk(PropertySetId), rootClass)!.RootParameterStoresComplete);
}

/// <summary>The stored Phase Created / Phase Demolished pass (<see cref="ElementBuilder.ApplyStoredPhases"/>).</summary>
public class StoredPhasesTests
{
    private const int PhaseCreated = -1012100, PhaseDemolished = -1012101;
    private const ushort ProjectPhaseClass = 3065;

    private static Element Instance(int? created, int? demolished)
    {
        var e = new Element { Id = 1 };
        e.Parameters.Add(BipHelpers.MakeBipParam(PhaseCreated, "PHASE_CREATED", StorageType.ElementId, -1));
        e.Parameters.Add(BipHelpers.MakeBipParam(PhaseDemolished, "PHASE_DEMOLISHED", StorageType.ElementId, 99));
        e.SerializedCreatedPhaseId = created;
        e.SerializedDemolishedPhaseId = demolished;
        return e;
    }

    private static object? Value(Element e, int id) => e.Parameters.Single(p => p.Id == id).Value;

    private static void Apply(Element e, IReadOnlyCollection<int>? order = null)
    {
        var info = new RevitFileInfo();
        info.Elements.Add(e);
        ElementBuilder.ApplyStoredPhases(info, new Dictionary<uint, ushort> { [86961] = ProjectPhaseClass, [5] = 100 }, order);
    }

    [Fact]
    public void DemolishedMinusOne_MeansNone_AndIsStored()
    {
        var e = Instance(86961, -1);
        Apply(e);
        Assert.Equal(86961, Value(e, PhaseCreated));
        Assert.Equal(-1, Value(e, PhaseDemolished));
    }

    [Fact]
    public void ADemolishedPhaseThatIsAProjectPhase_IsStored()
    {
        var e = Instance(86961, 86961);
        Apply(e);
        Assert.Equal(86961, Value(e, PhaseDemolished));
    }

    [Fact]
    public void AnIdOfAnotherClassOrAnUnknownId_LeavesTheParameterAlone()
    {
        var e = Instance(5, 77);
        Apply(e);
        Assert.Equal(-1, Value(e, PhaseCreated));
        Assert.Equal(99, Value(e, PhaseDemolished));
    }

    [Fact]
    public void AnEntryOfTheProjectPhaseOrder_IsAPhaseEvenWithoutAProjectPhaseRecord_AndZeroCounts()
    {
        var e = Instance(0, 2);
        Apply(e, [0, 2]);
        Assert.Equal(0, Value(e, PhaseCreated));
        Assert.Equal(2, Value(e, PhaseDemolished));
    }

    [Fact]
    public void WithoutTheClassTable_NothingChanges()
    {
        var e = Instance(86961, -1);
        var info = new RevitFileInfo();
        info.Elements.Add(e);
        ElementBuilder.ApplyStoredPhases(info, null);
        Assert.Equal(-1, Value(e, PhaseCreated));
        Assert.Equal(99, Value(e, PhaseDemolished));
    }
}
