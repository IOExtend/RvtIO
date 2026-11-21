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
        fixture.Add("StairsType", 30, w => { fixture.Id(w, 40); fixture.Id(w, 50); });
        fixture.Add("StairsRunType", 40);
        fixture.Add("StairsLandingType", 50);

        Assert.Equal(expected, fixture.TypeOf(10));
    }

    [Theory]
    [InlineData("FabricSheet", "FabricSheetType")]
    [InlineData("HandRail", "HandRailType")]
    [InlineData("TopRail", "TopRailType")]
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

    private sealed class Fixture
    {
        private readonly SchemaDocument _schema = new();
        private readonly List<PartitionFraming.Framed?> _frames = [];
        private readonly HashSet<long> _ids = [];
        private readonly int _width;

        internal Fixture(int width = 4)
        {
            _width = width;
            int identifier = Class("Identifier", 0, new SchemaField("m_id", (byte)(width == 8 ? 11 : 4), 0, 0, null, null, null, null));
            int elementId = Class("ElementId", 0, new SchemaField("m_id", 14, 0, 0, null, identifier, null, null));
            SchemaField Ref(string field, byte mods = 0) => new(field, 14, mods, 0, null, elementId, null, null);
            int element = Class("Element", 0, Ref("m_id"));
            int view = Class("DBView", element, Ref("m_dbViewTypeId"), Ref("m_viewerId"));
            int viewer = Class("Viewer", element, Ref("m_dbViewId"), new SchemaField("m_bIsViewerReference", 1, 0, 0, null, null, null, null));
            Class("Viewer3d", viewer);
            Class("DBView3d", view);
            Class("DBViewSection", view);
            Class("DBViewType", element);
            Class("InteriorElevArrow", viewer, Ref("m_parentId"));
            Class("InteriorElev", element, Ref("m_symbolId"), Ref("m_arrowIds", 0x50));
            int stairPart = Class("StairsRunAndLanding", element, Ref("m_stairsId"));
            Class("StairsRun", stairPart);
            Class("StairsLanding", stairPart);
            Class("StairsElement", element, Ref("m_typeId"));
            Class("StairsType", element, Ref("m_runTypeId"), Ref("m_landingTypeId"));
            Class("StairsRunType", element);
            Class("StairsLandingType", element);
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
            Class("FamilySymbol", element);
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

        internal void Add(string kind, long owner, Action<BinaryWriter>? payload = null, long? storedOwner = null)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            Id(writer, owner);
            writer.Write(0u);
            writer.Write(0u);
            writer.Write((ushort)_schema.Classes.Single(cls => cls.Name == kind).Id);
            Id(writer, storedOwner ?? owner);
            payload?.Invoke(writer);
            uint size = checked((uint)stream.Length - (uint)_width - 8);
            writer.Write(size);
            stream.Position = _width + 4;
            writer.Write(size);
            byte[] bytes = stream.ToArray();
            _frames.Add(new([bytes], [new(102, (ulong)owner, 0, bytes.Length)], _width));
            _ids.Add(owner);
        }

        internal long TypeOf(int owner, BuiltInCategory category = BuiltInCategory.INVALID, bool wasRouted = false)
        {
            var ids = new ElementIdMap(_ids.Select(id => new ElemTableLayout.Row(id, id, 0, 0, 0)).ToArray());
            var state = new ParsingState { ElementIds = ids, ElemTable = _ids.ToDictionary(id => (uint)ids.Key(id), _ => 0u) };
            if (wasRouted) state.IncrementRoutedPartitions = new Dictionary<ulong, int> { [(uint)owner] = 1 };
            var element = new Element { Id = owner, TypeId = 99, CategoryId = (int)category };
            FramedElementTypes.Apply(_schema, new Dictionary<Guid, SchemaField[]?>(), _frames, state, [element]);
            return ids.SourceId(element.TypeId);
        }
    }
}
