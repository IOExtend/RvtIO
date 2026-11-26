using RvtIO.BuiltIns;
using RvtIO.Internal;
using RvtIO.Model;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

public class FramedElementTypesTests
{
    [Theory]
    [InlineData(4, true)]
    [InlineData(8, true)]
    [InlineData(4, false)]
    public void AViewerNeedsTheBacklinkToItsOwnView(int width, bool reciprocal)
    {
        var fixture = new Fixture(width);
        long type = width == 8 ? (long)int.MaxValue + 20 : 30;
        fixture.Add("Viewer3d", 10, w => { fixture.Id(w, 20); w.Write(false); });
        fixture.Add("DBView3d", 20, w => { fixture.Id(w, type); fixture.Id(w, reciprocal ? 10 : 11); });
        fixture.Add("DBViewType", type);

        Assert.Equal(reciprocal ? type : 99, fixture.TypeOf(10));
    }

    [Theory]
    [InlineData(true, true, 70)]
    [InlineData(true, false, 99)]
    [InlineData(false, true, 99)]
    public void AReferencedElevationArrowNeedsAgreementBetweenBothMarkers(
        bool containsArrow, bool sameType, long referenceType)
    {
        var fixture = new Fixture();
        fixture.Add("DBViewSection", 10, w => { fixture.Id(w, -1); fixture.Id(w, 20); });
        fixture.Add("InteriorElevArrow", 20, w => { fixture.Id(w, 10); w.Write(false); fixture.Id(w, 30); });
        fixture.Add("InteriorElev", 30, w => { fixture.Id(w, 70); w.Write(1u); fixture.Id(w, 20); });
        fixture.Add("InteriorElevArrow", 40, w => { fixture.Id(w, 10); w.Write(true); fixture.Id(w, 50); });
        fixture.Add("InteriorElev", 50, w =>
        {
            fixture.Id(w, sameType ? 70 : 71);
            w.Write(1u);
            fixture.Id(w, containsArrow ? 40 : 41);
        });
        fixture.Add("DBViewType", 70);
        fixture.Add("DBViewType", 71);

        Assert.Equal(70, fixture.TypeOf(10));
        Assert.Equal(70, fixture.TypeOf(20));
        Assert.Equal(referenceType, fixture.TypeOf(40));
    }

    [Theory]
    [InlineData("StairsRun", 40)]
    [InlineData("StairsLanding", 50)]
    public void StairPartsTakeTheirTypeFromTheOwningStairType(string kind, long expected)
    {
        var fixture = new Fixture();
        fixture.Add(kind, 10, w => fixture.Id(w, 20));
        fixture.Add("StairsElement", 20, w => fixture.Id(w, 30));
        fixture.Add("StairsType", 30, w => fixture.StairsType(w, 40, 50));
        fixture.Add("StairsRunType", 40);
        fixture.Add("StairsLandingType", 50);

        Assert.Equal(expected, fixture.TypeOf(10));
    }

    [Theory]
    [InlineData("StairsRun", 20, false, false, 40, 40)]
    [InlineData("StairsLanding", 20, false, false, 40, 40)]
    [InlineData("StairsRun", 21, false, false, 40, 99)]
    [InlineData("StairsRun", 20, true, false, 40, 99)]
    [InlineData("StairsRun", 20, false, true, 40, 99)]
    [InlineData("StairsRun", 20, false, false, 41, 99)]
    public void AStairSupportNeedsMatchingSideTypesOnItsOwningStair(
        string hostKind, int hostStair, bool overridden, bool intermediate, int right, long expected)
    {
        var fixture = new Fixture();
        fixture.Add("StairsSupport", 10, w => { fixture.Id(w, 11); w.Write(overridden); });
        fixture.Add(hostKind, 11, w => fixture.Id(w, hostStair));
        fixture.Add("StairsElement", 20, w => fixture.Id(w, 30));
        fixture.Add("StairsType", 30, w => fixture.StairsType(w, -1, -1, 40, right, intermediate));
        fixture.Add("StairsSupportType", 40);

        Assert.Equal(expected, fixture.TypeOf(10, stairOwner: 20));
    }

    [Theory]
    [InlineData("FabricSheet", "FabricSheetType")]
    [InlineData("HandRail", "HandRailType")]
    [InlineData("TopRail", "TopRailType")]
    [InlineData("GridChain", "GridAttributes")]
    [InlineData("RebarContainer", "RebarContainerType")]
    [InlineData("RoofSoffit", "RoofSoffitType")]
    [InlineData("Ramp", "RampAttributes")]
    public void DirectLinksValidateTheDestinationClass(string kind, string target)
    {
        var fixture = new Fixture();
        fixture.Add(kind, 10, w => fixture.Id(w, 20));
        fixture.Add(kind, 11, w => fixture.Id(w, 21));
        fixture.Add(target, 20);
        fixture.Add("DBViewType", 21);

        Assert.Equal(20, fixture.TypeOf(10));
        Assert.Equal(99, fixture.TypeOf(11));
    }

    [Theory]
    [InlineData(true, 20)]
    [InlineData(false, 99)]
    public void RebarTypeComesFromItsOwnInstancePointer(bool hasInstance, long expected)
    {
        var fixture = new Fixture();
        fixture.Add("Rebar", 10, w =>
        {
            fixture.Pointer(w, "RebarInstance");
            fixture.Pointer(w, hasInstance ? "RebarInstance" : null);
            fixture.Id(w, 21);
            if (hasInstance) fixture.Id(w, 20);
        });
        fixture.Add("RebarBarType", 20);
        fixture.Add("RebarBarType", 21);

        Assert.Equal(expected, fixture.TypeOf(10));
    }

    [Theory]
    [InlineData(20, 20)]
    [InlineData(21, 99)]
    public void ASpanTagNeedsBothStoredSymbolFieldsToAgree(int actual, long expected)
    {
        var fixture = new Fixture();
        fixture.Add("SpanDirectionSymbol", 10, w => { fixture.Id(w, 20); fixture.Id(w, actual); });
        fixture.Add("FamilySymbol", 20);
        fixture.Add("FamilySymbol", 21);

        Assert.Equal(expected, fixture.TypeOf(10));
    }

    [Theory]
    [InlineData(false, false, BuiltInCategory.OST_DetailComponents, 20)]
    [InlineData(true, false, BuiltInCategory.OST_DetailComponents, 99)]
    [InlineData(false, true, BuiltInCategory.OST_DetailComponents, 99)]
    [InlineData(false, false, BuiltInCategory.OST_Doors, 99)]
    public void ADetailComponentMustHaveOneUnroutedOwnRecord(
        bool duplicate, bool wasRouted, BuiltInCategory category, long expected)
    {
        var fixture = new Fixture();
        fixture.Add("FamilyInstance", 10, w => fixture.Id(w, 20));
        if (duplicate) fixture.Add("FamilyInstance", 10, w => fixture.Id(w, 20));
        fixture.Add("FamilySymbol", 20);

        Assert.Equal(expected, fixture.TypeOf(10, category, wasRouted));
    }

    [Theory]
    [InlineData(4, 1, false, false, false, true)]
    [InlineData(8, 1, false, false, false, true)]
    [InlineData(4, 0, false, false, false, false)]
    [InlineData(4, 1, true, false, false, false)]
    [InlineData(4, 1, false, true, false, false)]
    [InlineData(4, 1, false, false, true, false)]
    public void AnUnroutedFamilyRecoversOnlyTypeIdFromTheLatestHolder(
        int width, int routedIncrement, bool permutation, bool wrongType, bool ambiguous, bool corrected)
    {
        var fixture = new Fixture(width);
        long latest = width == 8 ? (long)int.MaxValue + 50 : 21;
        fixture.Add("FamilyInstance", 10, w => fixture.Id(w, latest), partition: 2);
        fixture.Add("FamilyInstance", 10, w => fixture.Id(w, 20), partition: 0);
        if (ambiguous) fixture.Add("FamilyInstance", 10, w => fixture.Id(w, 20), partition: 2);
        fixture.Add("FamilySymbol", 20);
        fixture.Add(wrongType ? "Other" : "SysPanelFamSym", latest);

        var rows = new[] { new ElemTableLayout.Row(10, 10, 0, 0, -1, routedIncrement) };
        var increments = Enumerable.Range(0, 3)
            .Select(i => new DocumentIncrementTable.Increment(i, 1, 1, [])).ToArray();
        var table = new DocumentIncrementTable.Table(increments, increments, permutation ? [(1, 2)] : []);
        var typePartitions = FramedElementTypes.TypePartitionsWithoutRoutedCopy(
            new Dictionary<ulong, List<int>> { [10] = [0, 2] }, rows, table, new Dictionary<ulong, int>());
        var element = new Element
        {
            Id = 10, TypeId = 20, Name = "Earlier name", HostId = 32,
            Parameters = [new Parameter { Definition = new Definition { Name = "ELEM_TYPE_PARAM" }, Value = 20 }],
        };
        var parameters = element.Parameters;

        Assert.Equal(corrected ? latest : 20, fixture.Apply(element, typePartitions: typePartitions));
        Assert.Equal("Earlier name", element.Name);
        Assert.Equal(32, element.HostId);
        Assert.Equal(20, element.FamilyTypeId);
        Assert.Same(parameters, element.Parameters);
    }

    [Fact]
    public void OtherOrInvalidProjectRecordsPreventTypeRecovery()
    {
        var fixture = new Fixture();
        fixture.Add("FabricSheet", 10, w => fixture.Id(w, 20));
        fixture.Add("Other", 10);
        fixture.Add("FabricSheet", 11, w => fixture.Id(w, 20), storedOwner: 18);
        fixture.Add("FabricSheet", 12, w => { fixture.Id(w, 20); w.Write((byte)0); });
        fixture.Add("FabricSheetType", 20);

        Assert.All(new[] { 10, 11, 12 }, id => Assert.Equal(99, fixture.TypeOf(id)));
    }

    [Theory]
    [InlineData("LinearDimString", "DimensionStyle", -1)]
    [InlineData("TextNote", "TextNoteAttributes", -1)]
    [InlineData("SWall", "BasicWallType", 30)]
    public void KeepCopiesRestoresOnlyTypeIdFromASingleUnanchoredRecord(string kind, string target, int oldType)
    {
        var fixture = new Fixture();
        fixture.Add(kind, 10, w => fixture.Id(w, 20), partition: 1);
        fixture.Add(target, 20, partition: 0);
        var element = new Element { Id = 10, TypeId = oldType, Name = "Stored name", HostId = 45 };
        element.Parameters.Add(new Parameter { Value = "Stored parameter" });

        Assert.Equal(20, fixture.Apply(element, recoverUnanchoredTypes: true));
        Assert.Equal("Stored name", element.Name);
        Assert.Equal(45, element.HostId);
        Assert.Equal("Stored parameter", Assert.Single(element.Parameters).Value);
    }

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, true, false, false)]
    [InlineData(true, false, true, false)]
    [InlineData(true, false, false, true)]
    public void UnanchoredWallRequiresKeepModeAnUnambiguousRootAndARealType(
        bool keepCopies, bool anchored, bool otherCopy, bool wrongTarget)
    {
        var fixture = new Fixture();
        fixture.Add("SWall", 10, w => fixture.Id(w, 20), partition: 1);
        if (otherCopy) fixture.Add("SWall", 10, w => fixture.Id(w, 30), partition: 0);
        if (wrongTarget) fixture.Add("SWall", 20, w => fixture.Id(w, 30));
        else fixture.Add("BasicWallType", 20);
        fixture.Add("BasicWallType", 30);

        var element = new Element { Id = 10, TypeId = 30 };
        Assert.Equal(30, fixture.Apply(element, recoverUnanchoredTypes: keepCopies, anchored: anchored));
    }

    [Theory]
    [InlineData("SWall", "BasicWallType", 20L, 4)]
    [InlineData("SWall", "NewCurtainWallType", 20L, 4)]
    [InlineData("FamilyInstance", "SysPanelFamSym", 2147483668L, 8)]
    [InlineData("RoomTag", "FamilySymbol", 20L, 4)]
    [InlineData("ProfileRoof", "RoofAttributes", 20L, 4)]
    [InlineData("LinearDimString", "DimensionStyle", 20L, 4)]
    [InlineData("Floor", "FloorAttributes", 20L, 4)]
    [InlineData("DBViewPlan", "DBViewType", 20L, 4)]
    [InlineData("DBViewSection", "DBViewType", 20L, 4)]
    [InlineData("BaseRailing", "StairsRailingAttr", 20L, 4)]
    public void KeepCopiesUsesTheRoutedTypeWithoutReplacingOtherFields(string kind, string target, long type, int width)
    {
        var fixture = new Fixture(width);
        void WriteOccurrence(BinaryWriter writer, long storedType)
        {
            fixture.Id(writer, storedType);
            if (kind is "DBViewPlan" or "DBViewSection") fixture.Id(writer, -1);
        }

        fixture.Add(kind, 10, w => WriteOccurrence(w, 30), partition: 0);
        fixture.Add(kind, 10, w => WriteOccurrence(w, type), partition: 1);
        fixture.Add(target, type);
        var element = new Element { Id = 10, TypeId = 30, Name = "Historical copy", HostId = 40 };

        Assert.Equal(type, fixture.Apply(element, wasRouted: true, recoverUnanchoredTypes: true, anchored: true));
        Assert.Equal("Historical copy", element.Name);
        Assert.Equal(40, element.HostId);
    }

    [Theory]
    [InlineData("ordinary mode")]
    [InlineData("no route")]
    [InlineData("missing current partition")]
    [InlineData("duplicate current partition")]
    [InlineData("wrong owner")]
    [InlineData("different root")]
    [InlineData("missing type")]
    [InlineData("different target class")]
    [InlineData("unsupported target copy")]
    [InlineData("unrelated old type")]
    public void RoutedTypeRequiresExactCopiesAStoredOldTypeAndARealTarget(string reason)
    {
        var fixture = new Fixture();
        fixture.Add("SWall", 10, w => fixture.Id(w, 30), partition: 0);
        if (reason == "different root") fixture.Add("Other", 10, partition: 1);
        else fixture.Add("SWall", 10, w => fixture.Id(w, 20),
            storedOwner: reason == "wrong owner" ? 11 : null, partition: reason == "missing current partition" ? 2 : 1);
        if (reason == "duplicate current partition")
            fixture.Add("SWall", 10, w => fixture.Id(w, 20), partition: 1);
        if (reason != "missing type")
            fixture.Add(reason == "different target class" ? "DimensionStyle" : "BasicWallType", 20);
        if (reason == "unsupported target copy") fixture.Add("Other", 20);
        fixture.Add("BasicWallType", 30);

        int oldType = reason == "unrelated old type" ? 40 : 30;
        var element = new Element { Id = 10, TypeId = oldType };
        Assert.Equal(oldType, fixture.Apply(element, wasRouted: reason != "no route",
            recoverUnanchoredTypes: reason != "ordinary mode", anchored: true));
    }

    [Theory]
    [InlineData(20, 20)]
    [InlineData(-1, 30)]
    public void RoutedGroupTypeComesFromIts2022MembershipCell(int currentType, int expectedType)
    {
        using var resource = typeof(TfBlockDeserializer).Assembly.GetManifestResourceStream("RvtIO.Resources.r22_schemas.json")!;
        var schema = TfSchemaReader.ReadShipped2022(resource);
        // The shipped table preserves the transform dimensions, but omits the template argument.
        var fields = schema.Classes.Single(cls => cls.Name == "Trf").Fields;
        int matrix = fields.FindIndex(field => field.Name == "m_3x3");
        fields[matrix] = fields[matrix] with { Args = [new("", 7, 16, 0, 3, null, null, null)] };
        var fixture = new Fixture(schema: schema);

        void AddGroup(int type, int partition)
        {
            fixture.AddBlock("ElementGroup", 10, writer =>
            {
                foreach (var field in TfBlockDeserializer.SerializationFields(1322)!)
                {
                    if (field is { Declaring: 37, Name: "m_cellList" }) fixture.Pointer(writer, "CellList");
                    else if (field is { Declaring: 37, Name: "m_id" }) writer.Write(10);
                    else WriteDefault2022Field(writer, field);
                }
                writer.Write(1u);
                fixture.Pointer(writer, "GroupMembership");
                writer.Write(type);
            }, partition);
        }

        AddGroup(30, 0);
        AddGroup(currentType, 1);
        fixture.AddBlock("ElementGroupType", 20, writer =>
        {
            foreach (var field in TfBlockDeserializer.SerializationFields(1333)!)
                if (field is { Declaring: 37, Name: "m_id" }) writer.Write(20);
                else WriteDefault2022Field(writer, field);
        });

        Assert.Equal(expectedType, fixture.Apply(new Element { Id = 10, TypeId = 30 },
            wasRouted: true, recoverUnanchoredTypes: true));
    }

    private static void WriteDefault2022Field(BinaryWriter writer, TfBlockDeserializer.TfField field)
    {
        if (field.Mods >> 4 == 5) { writer.Write(0u); return; }
        int count = field.Mods >> 4 == 1 ? field.Cnt!.Value : 1;
        for (int i = 0; i < count; i++)
            switch (field.Type)
            {
                case 1: writer.Write((byte)0); break;
                case 4: writer.Write(0); break;
                case 7: writer.Write(0d); break;
                case 13 when field is { Declaring: 78, Name: "m_3x3" }:
                    for (int column = 0; column < 3; column++) writer.Write(i == column ? 1d : 0d);
                    break;
                case 14 when (field.Mods & 15) == 0 && field.Ref is int reference && reference != 20:
                    foreach (var nested in TfBlockDeserializer.SerializationFields(reference)!) WriteDefault2022Field(writer, nested);
                    break;
                case 14: writer.Write(field.Ref == 20 ? -1 : 0); break;
                default: throw new InvalidOperationException(field.Name);
            }
    }

    private sealed class Fixture
    {
        private readonly SchemaDocument _schema;
        private readonly List<PartitionFraming.Framed?> _frames = [];
        private readonly HashSet<long> _ids = [];
        private readonly int _width;

        internal Fixture(int width = 4, SchemaDocument? schema = null)
        {
            _width = width;
            _schema = schema ?? new();
            if (schema != null) return;
            int identifier = Class("Identifier", 0, new SchemaField("m_id", (byte)(width == 8 ? 11 : 4), 0, 0, null, null, null, null));
            int elementId = Class("ElementId", 0, new SchemaField("m_id", 14, 0, 0, null, identifier, null, null));
            SchemaField Ref(string field, byte mods = 0) => new(field, 14, mods, 0, null, elementId, null, null);
            SchemaField Int(string field) => new(field, 4, 0, 0, null, null, null, null);
            SchemaField Bool(string field) => new(field, 1, 0, 0, null, null, null, null);
            SchemaField Owned(string field) => new(field, 14, 1, 0, null, null, null, null);
            int element = Class("Element", 0, Ref("m_id"));
            int view = Class("DBView", element, Ref("m_dbViewTypeId"), Ref("m_viewerId"));
            int viewer = Class("Viewer", element, Ref("m_dbViewId"), new SchemaField("m_bIsViewerReference", 1, 0, 0, null, null, null, null));
            Class("Viewer3d", viewer);
            Class("DBView3d", view);
            Class("DBViewPlan", view);
            Class("DBViewSection", view);
            Class("DBViewType", element);
            Class("InteriorElevArrow", viewer, Ref("m_parentId"));
            Class("InteriorElev", element, Ref("m_symbolId"), Ref("m_arrowIds", 0x50));
            int stairPart = Class("StairsRunAndLanding", element, Ref("m_stairsId"));
            Class("StairsRun", stairPart);
            Class("StairsLanding", stairPart);
            Class("StairsElement", element, Ref("m_typeId"));
            Class("StairsType", element, Ref("m_runTypeId"), Ref("m_landingTypeId"), Ref("m_leftSideSupportTypeId"),
                Ref("m_rightSideSupportTypeId"), Int("m_leftSupportStyle"), Int("m_rightSupportStyle"), Bool("m_hasIntermediateSupport"));
            Class("StairsRunType", element);
            Class("StairsLandingType", element);
            Class("StairsSupport", element, Ref("m_hostCompId"), Bool("m_isTypeOverridden"));
            Class("StairsSupportType", element);
            Class("FabricSheet", element, Ref("m_sheetTypeId"));
            Class("FabricSheetType", element);
            int rail = Class("ContinuousRail", element, Ref("m_typeId"));
            foreach (string kind in new[] { "HandRail", "TopRail" })
            {
                Class(kind, rail);
                Class(kind + "Type", element);
            }
            int tag = Class("IndependentTag", element, Ref("m_symbolId"));
            Class("SpanDirectionSymbol", tag, Ref("m_actualSymbolId"));
            Class("FamilyInstance", element, Ref("m_masterSymbolId"));
            Class("SysPanelFamSym", Class("FamilySymbol", element));
            Class("GridChain", element, Ref("m_typeId"));
            Class("GridAttributes", element);
            Class("RebarContainer", element, Ref("m_rebarContainerTypeId"));
            Class("RebarContainerType", element);
            Class("Rebar", element, Owned("m_other"), Owned("m_oInstance"));
            Class("RebarInstance", Class("RebarInstBase", 0, Ref("m_barTypeId")));
            Class("RebarBarType", element);
            Class("RoofSoffit", Class("Ceiling", element, Ref("m_ceilingAttributesId")));
            Class("RoofSoffitType", element);
            Class("Ramp", element, Ref("m_rampAttrId"));
            Class("RampAttributes", element);
            Class("RoomTag", element, Ref("m_symbolId"));
            Class("ProfileRoof", Class("RoofBase", element, Ref("m_roofAttributesId")));
            Class("RoofAttributes", element);
            Class("Floor", element, Ref("m_floorAttributesId"));
            Class("FloorAttributes", element);
            Class("BaseRailing", element, Ref("m_stairsRailingAttrId"));
            Class("StairsRailingAttr", element);
            Class("LinearDimString", Class("Dimension", element, Ref("m_styleSymbolId")));
            Class("DimensionStyle", element);
            Class("TextNote", Class("TextElement", element, Ref("m_attrId")));
            Class("TextNoteAttributes", element);
            Class("SWall", Class("VWall", element, Ref("m_WallAttributesId")));
            Class("BasicWallType", element);
            Class("NewCurtainWallType", element);
            Class("Other", element);
        }

        private int Class(string name, int baseId, params SchemaField[] fields)
        {
            int id = 12 + _schema.Classes.Count;
            _schema.Classes.Add(new(id, name, baseId, 1, [.. fields], []));
            return id;
        }

        internal void Id(BinaryWriter writer, long id)
        {
            if (_width == 4) writer.Write(checked((int)id));
            else writer.Write(id);
        }

        internal void StairsType(BinaryWriter writer, long run, long landing, long left = -1, long right = -1, bool intermediate = false)
        {
            Id(writer, run);
            Id(writer, landing);
            Id(writer, left);
            Id(writer, right);
            writer.Write(1);
            writer.Write(1);
            writer.Write(intermediate);
        }

        internal void Pointer(BinaryWriter writer, string? kind)
        {
            writer.Write(kind == null ? 0u : uint.MaxValue);
            if (kind != null) writer.Write((ushort)_schema.Classes.Single(cls => cls.Name == kind).Id);
        }

        internal void Add(string kind, long owner, Action<BinaryWriter>? payload = null, long? storedOwner = null,
            int? partition = null)
            => AddBlock(kind, owner, writer => { Id(writer, storedOwner ?? owner); payload?.Invoke(writer); }, partition);

        internal void AddBlock(string kind, long owner, Action<BinaryWriter> payload, int? partition = null)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            Id(writer, owner);
            writer.Write(0u);
            writer.Write(0u);
            writer.Write((ushort)_schema.Classes.Single(cls => cls.Name == kind).Id);
            payload(writer);
            uint size = checked((uint)stream.Length - (uint)_width - 8);
            writer.Write(size);
            stream.Position = _width + 4;
            writer.Write(size);
            byte[] bytes = stream.ToArray();
            _frames.Add(new([bytes], [new(102, (ulong)owner, 0, bytes.Length)], _width, partition));
            _ids.Add(owner);
        }

        internal long TypeOf(int owner, BuiltInCategory category = BuiltInCategory.INVALID, bool wasRouted = false, int? stairOwner = null)
            => Apply(new Element { Id = owner, TypeId = 99, CategoryId = (int)category }, wasRouted, stairOwner);

        internal long Apply(Element element, bool wasRouted = false, int? stairOwner = null,
            IReadOnlyDictionary<ulong, int>? typePartitions = null, bool recoverUnanchoredTypes = false, bool anchored = false)
        {
            var ids = new ElementIdMap(_ids.Select(id => new ElemTableLayout.Row(id, id, 0, 0, 0)).ToArray());
            var state = new ParsingState { ElementIds = ids, ElemTable = _ids.ToDictionary(id => (uint)ids.Key(id), _ => 0u) };
            if (wasRouted) state.IncrementRoutedPartitions = new Dictionary<ulong, int> { [(uint)element.Id] = 1 };
            if (stairOwner is int stair) state.ElemRecOwningElementIds = new Dictionary<uint, int> { [(uint)element.Id] = stair };
            if (anchored) state.NativeParamsFromRootClass.Add((uint)element.Id);
            var entities = new Dictionary<Guid, SchemaField[]?>();
            var retained = FramedElementTypes.Prepare(_schema, entities, _frames, state, typePartitions, recoverUnanchoredTypes);
            var copy = element.ShallowCopy();
            int lateChanges = retained?.Apply(state, [copy], typePartitions, recoverUnanchoredTypes) ?? 0;
            Assert.Equal(FramedElementTypes.Apply(_schema, entities, _frames, state, [element], typePartitions, recoverUnanchoredTypes), lateChanges);
            Assert.Equal(element.TypeId, copy.TypeId);
            return ids.SourceId(element.TypeId);
        }
    }
}
