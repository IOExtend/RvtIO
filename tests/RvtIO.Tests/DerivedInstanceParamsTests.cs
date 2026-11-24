using RvtIO.Model;
using RvtIO.Parsing;
using RvtIO.Internal;
using Xunit;

namespace RvtIO.Tests;

public class DerivedInstanceParamsTests
{
    [Fact]
    public void RoomSeparationLineDoesNotUseItsNeighborAsAType()
    {
        var line = new Element { Id = 1, CategoryId = -2000066, TypeId = -1, References = [2] };
        var neighbor = new Element { Id = 2, CategoryId = -2000066, TypeId = -1 };

        ElementBuilder.PostProcess(new RevitFileInfo { Elements = [line, neighbor] });

        Assert.Equal(-1L, line.TypeElementId);
    }

    private static Parameter P(int id, StorageType storage, object value) => BipHelpers.MakeBipParam(id, id.ToString(), storage, value);
    private static object? Value(Element e, int id) => e.Parameters.FirstOrDefault(p => p.Id == id)?.Value;

    private static Element Level(int id, double elevation, string name = "L", double computationHeight = 0)
    {
        var level = new Element { Id = id, IsNativeLevel = true };
        level.Parameters.Add(P(-1007102, StorageType.Double, elevation));
        level.Parameters.Add(P(-1007101, StorageType.String, name));
        level.Parameters.Add(P(-1006939, StorageType.Double, computationHeight));
        return level;
    }

    [Fact]
    public void HostId_FreeHostIsMinusOne_HostBasedFamilyAndCurtainMembersKeepTheirHost()
    {
        var family = new Element { Id = 1, FamilyIsHostBased = false };
        var hostedFamily = new Element { Id = 2, FamilyIsHostBased = true };
        var symbol = new Element { Id = 3, SymbolFamilyId = 1 };
        var hostedSymbol = new Element { Id = 4, SymbolFamilyId = 2 };
        var floor = new Element { Id = 10, Category = "OST_Floors" };
        var wall = new Element { Id = 11, Category = "OST_Walls" };
        var onFloor = new Element { Id = 20, SerializedHostId = 10, NativeMasterSymbolId = 3 };
        var inWall = new Element { Id = 21, SerializedHostId = 11, NativeMasterSymbolId = 4 };
        var panel = new Element { Id = 22, SerializedHostId = 11, NativeMasterSymbolId = 3, IsCurtainMemberInstance = true };
        var onPlane = new Element { Id = 23, SerializedHostId = 30, NativeMasterSymbolId = 4 };
        var info = new RevitFileInfo { Elements = [family, hostedFamily, symbol, hostedSymbol, floor, wall, onFloor, inWall, panel, onPlane] };

        ElementBuilder.ApplyHostIdParam(info, new Dictionary<uint, ushort> { [30] = 3607 });

        Assert.Equal(-1, Value(onFloor, -1002108));
        Assert.Equal(11, Value(inWall, -1002108));
        Assert.Equal(11, Value(panel, -1002108));
        Assert.Equal(-1, Value(onPlane, -1002108));
    }

    [Fact]
    public void InstanceOrigin_GivesOffsetFromHost_AndFoundationHeightOffset()
    {
        var chair = new Element { Id = 1, NativeMasterSymbolId = 5, InstanceOriginZ = 1.5 };
        chair.Parameters.Add(P(-1001364, StorageType.Double, 9.0));
        var footing = new Element { Id = 2, NativeMasterSymbolId = 5, InstanceOriginZ = -2.0, CategoryId = -2001300 };
        footing.Parameters.Add(P(-1001364, StorageType.Double, 9.0));
        footing.Parameters.Add(P(-1001360, StorageType.Double, 9.0));

        ElementBuilder.ApplyInstanceOriginOffsets(new RevitFileInfo { Elements = [chair, footing] });

        Assert.Equal(1.5, Value(chair, -1001364));
        Assert.Equal(-2.0, Value(footing, -1001951));
        Assert.Null(Value(footing, -1001364));
        Assert.Null(Value(footing, -1001360));
    }

    [Fact]
    public void StructuralColumn_BaseOffsetStyleAndLength()
    {
        var column = new Element { Id = 1, CategoryId = -2001330, NativeMasterSymbolId = 5,
            FamilyLevelRefs = (10, 0.5, 11, -0.25), InstanceIsVertical = true };
        var info = new RevitFileInfo { Elements = [Level(10, 0), Level(11, 12), column] };

        ElementBuilder.ApplyStructuralColumnParams(info);

        Assert.Equal(0.5, Value(column, -1002065));
        Assert.Equal(0.5, Value(column, -1001357));
        Assert.Equal(0, Value(column, -1150171));
        Assert.Equal(11.25, Value(column, -1001375));
    }

    [Fact]
    public void ArchitecturalColumn_BaseOffsetAndLength()
    {
        var column = new Element { Id = 1, CategoryId = -2000100, NativeMasterSymbolId = 5,
            FamilyLevelRefs = (10, 1.0, 11, 0.5), InstanceIsVertical = true };
        var info = new RevitFileInfo { Elements = [Level(10, 0), Level(11, 10), column] };

        ElementBuilder.ApplyStructuralColumnParams(info);

        Assert.Equal(1.0, Value(column, -1002065));
        Assert.Equal(1.0, Value(column, -1001357));
        Assert.Null(Value(column, -1150171));
        Assert.Equal(9.5, Value(column, -1001375));
    }

    [Fact]
    public void RoomLevelNameAndComputationHeight()
    {
        var room = new Element { Id = 1, CategoryId = -2000160 };
        room.Parameters.Add(P(-1006916, StorageType.ElementId, 10));
        var info = new RevitFileInfo { Elements = [Level(10, 0, "Ground", 3.5), room] };

        ElementBuilder.ApplyRoomLevelParams(info);

        Assert.Equal("Ground", Value(room, -1007101));
        Assert.Equal(3.5, Value(room, -1006928));
    }

    [Fact]
    public void FramingLength_ComesFromTheInstanceSymbol()
    {
        var symbol = new Element { Id = 2, SymbolFramingLength = 11.25 };
        var beam = new Element { Id = 1, CategoryId = -2001320, NativeMasterSymbolId = 5, InstanceSymbolId = 2 };
        beam.Parameters.Add(P(-1001375, StorageType.Double, 4.92));

        ElementBuilder.ApplyFramingSymbolLength(new RevitFileInfo { Elements = [symbol, beam] });

        Assert.Equal(11.25, Value(beam, -1001375));
    }

    [Fact]
    public void RoomLoads_ComeFromTheProjectBuildingType()
    {
        var settings = new Element { Id = 1, EnergyBuildingTypeId = 2 };
        var buildingType = new Element { Id = 2 };
        buildingType.Parameters.Add(P(-1114239, StorageType.Double, 3.0));
        buildingType.Parameters.Add(P(-1114189, StorageType.Double, 2.0));
        var room = new Element { Id = 3, CategoryId = -2000160 };

        ElementBuilder.ApplyRoomBuildingTypeLoads(new RevitFileInfo { Elements = [settings, buildingType, room] });

        Assert.Equal(3.0, Value(room, -1114239));
        Assert.Equal(5.0, Value(room, -1114188));
    }

    [Fact]
    public void HostLabels_UseTheLocalizedTextsAndDropUnvalidatedHosts()
    {
        var names = RvtIO.Lookup.RevitSystemFamilyNames.ForTests(
            new Dictionary<RvtIO.Lookup.SystemFamilyKind, string>(), null,
            new Dictionary<RvtIO.Lookup.RevitUiText, string>
            {
                [RvtIO.Lookup.RevitUiText.LevelFamily] = "Уровень",
                [RvtIO.Lookup.RevitUiText.TopographySurface] = "Поверхность",
                [RvtIO.Lookup.RevitUiText.Subregion] = "Область поверхности",
            });
        var level = Level(10, 0, "L1");
        var surface = new Element { Id = 20, CategoryId = -2001340, SerializedHostId = -1 };
        var subregion = new Element { Id = 21, CategoryId = -2001340, SerializedHostId = 20 };
        var floor = new Element { Id = 22, CategoryId = -2000032 };
        Element Instance(int id, int host)
        {
            var e = new Element { Id = id, SerializedHostId = host, NativeMasterSymbolId = 5 };
            e.Parameters.Add(P(-1001352, StorageType.ElementId, 10));
            e.Parameters.Add(P(-1001363, StorageType.String, "legacy"));
            return e;
        }
        var free = Instance(30, -1);
        var onSurface = Instance(31, 20);
        var onSubregion = Instance(32, 21);
        var onFloor = Instance(33, 22);
        var info = new RevitFileInfo { Elements = [level, surface, subregion, floor, free, onSurface, onSubregion, onFloor] };

        ElementBuilder.ApplyHostLabels(info, names, new Dictionary<uint, ushort> { [20] = 3595, [21] = 3595 });

        Assert.Equal("Уровень : L1", Value(free, -1001363));
        var floorOnLevel = new Element { Id = 34 };
        floorOnLevel.Parameters.Add(P(-1001352, StorageType.ElementId, 10));
        floorOnLevel.Parameters.Add(P(-1001363, StorageType.String, "legacy"));
        ElementBuilder.ApplyHostLabels(new RevitFileInfo { Elements = [level, floorOnLevel] }, names, new Dictionary<uint, ushort>());
        Assert.Null(Value(floorOnLevel, -1001363)); // not a family instance: no Host
        Assert.Equal("Поверхность", Value(onSurface, -1001363));
        Assert.Equal("Область поверхности", Value(onSubregion, -1001363));
        Assert.Null(Value(onFloor, -1001363));
    }

    [Fact]
    public void ConnectionParams_OnlyForFamiliesWithProjectSymbolicRepresentation()
    {
        var family = new Element { Id = 1 };
        family.Parameters.Add(P(-1005197, StorageType.Integer, 1));
        var otherFamily = new Element { Id = 2 };
        otherFamily.Parameters.Add(P(-1005197, StorageType.Integer, 0));
        var symbol = new Element { Id = 3, SymbolFamilyId = 1 };
        var otherSymbol = new Element { Id = 4, SymbolFamilyId = 2 };
        var beam = new Element { Id = 10, CategoryId = -2001320, NativeMasterSymbolId = 3, StructuralConnectionTypes = (-1, 77) };
        var column = new Element { Id = 11, CategoryId = -2001330, NativeMasterSymbolId = 3, StructuralConnectionTypes = (5, 6) };
        var otherBeam = new Element { Id = 12, CategoryId = -2001320, NativeMasterSymbolId = 4, StructuralConnectionTypes = (-1, -1) };

        ElementBuilder.ApplyStructuralConnectionParams(new RevitFileInfo { Elements = [family, otherFamily, symbol, otherSymbol, beam, column, otherBeam] });

        Assert.Equal(-1, Value(beam, -1018801));
        Assert.Equal(77, Value(beam, -1018802));
        Assert.Equal(5, Value(column, -1018804));
        Assert.Equal(6, Value(column, -1018803));
        Assert.Empty(otherBeam.Parameters);
    }

    [Fact]
    public void HeadHeight_IsSillPlusTypeHeight_IgnoringBoundingBoxEstimates()
    {
        var type = new Element { Id = 5 };
        type.Parameters.Add(P(-1001300, StorageType.Double, 7.0));
        var door = new Element { Id = 1, CategoryId = -2000023, NativeMasterSymbolId = 5,
            BoundingBoxDimensionParamIds = [-1001300] };
        door.Parameters.Add(P(-1001361, StorageType.Double, 0.5));
        door.Parameters.Add(P(-1001300, StorageType.Double, 7.3)); // bounding-box estimate

        ElementBuilder.ApplyHeadHeight(new RevitFileInfo { Elements = [type, door] });

        Assert.Equal(7.5, Value(door, -1001362));
    }

    [Fact]
    public void HostLabels_NameAFreeHostByItsFamilyAndType()
    {
        var names = RvtIO.Lookup.RevitSystemFamilyNames.ForTests(
            new Dictionary<RvtIO.Lookup.SystemFamilyKind, string>(), null,
            new Dictionary<RvtIO.Lookup.RevitUiText, string> { [RvtIO.Lookup.RevitUiText.LevelFamily] = "Level" });
        var floorType = new Element { Id = 40, Name = "Generic 150mm", ValidatedSystemFamilyName = "Floor" };
        var floor = new Element { Id = 41, TypeId = 40 };
        var loadableType = new Element { Id = 42, Name = "Steel", FamilyName = "Sink" };
        var sink = new Element { Id = 43, TypeId = 42 };
        Element Instance(int id, int host, int hostIdParam)
        {
            var e = new Element { Id = id, SerializedHostId = host };
            e.Parameters.Add(P(-1001363, StorageType.String, "legacy"));
            e.Parameters.Add(P(-1002108, StorageType.ElementId, hostIdParam));
            return e;
        }
        var panelType = new Element { Id = 44, Name = "100 A", FamilyName = "Panelboard" };
        var panel = new Element { Id = 45, TypeId = 44 };
        panel.Parameters.Add(P(-1140078, StorageType.String, "LP-2B"));
        var onPanel = Instance(53, 45, -1);
        var namedPanel = new Element { Id = 46, TypeId = 44, CategoryId = -2001040, Name = "MP-2B" };
        var onNamedPanel = Instance(54, 46, -1);
        var unnamedPanel = new Element { Id = 47, TypeId = 44, CategoryId = -2001040, Name = "100 A" };
        var onUnnamedPanel = Instance(55, 47, -1);
        var onFloor = Instance(50, 41, -1);
        var onSink = Instance(51, 43, -1);
        var inWall = Instance(52, 41, 41); // hosted for real: Host Id names the host, no Host text
        var info = new RevitFileInfo { Elements = [floorType, floor, loadableType, sink, panelType, panel, namedPanel, unnamedPanel, onFloor, onSink, onPanel, onNamedPanel, onUnnamedPanel, inWall] };

        ElementBuilder.ApplyHostLabels(info, names, new Dictionary<uint, ushort>());

        Assert.Equal("Floor : Generic 150mm", Value(onFloor, -1001363));
        Assert.Equal("Sink : Steel", Value(onSink, -1001363));
        Assert.Equal("Panelboard : LP-2B", Value(onPanel, -1001363));
        Assert.Equal("Panelboard : MP-2B", Value(onNamedPanel, -1001363));
        Assert.Null(Value(onUnnamedPanel, -1001363));
        Assert.Null(Value(inWall, -1001363));
    }

    private static Element Beam(double[][] vertices, (double X, double Y, double Z)? direction, double? coping)
    {
        var beam = new Element
        {
            Id = 1, CategoryId = -2001320, NativeMasterSymbolId = 5,
            CurveDriverLineDirection = direction, FramingCopingDistance = coping,
            Geometry = new GeometryData { Faces = [new GeoFace { OuterVertices = vertices.ToList() }] },
        };
        return beam;
    }

    [Fact]
    public void DiagnosticFramingCutLength_IsTheExtentOfTheSolidsAlongTheDriveLine()
    {
        var beam = Beam([[0, 0, 0], [3, 4, 0], [6, 8, 0], [6, 8, 1]], (0.6, 0.8, 0), -1);

        ElementBuilder.ApplyFramingCutLength(new RevitFileInfo { Elements = [beam] });

        Assert.Equal(10.0, (double)Value(beam, -1001384)!, 9);
    }

    [Fact]
    public void FramingCutLength_IsLeftOutForIndependentEndJustification()
    {
        var independent = Beam([[0, 0, 0], [5, 0, 0]], (1, 0, 0), -1);
        independent.Parameters.Add(P(-1152361, StorageType.Integer, 1));
        var uniform = Beam([[0, 0, 0], [5, 0, 0]], (1, 0, 0), -1);
        uniform.Parameters.Add(P(-1152361, StorageType.Integer, 0));

        ElementBuilder.ApplyFramingCutLength(new RevitFileInfo { Elements = [independent, uniform] });

        Assert.Null(Value(independent, -1001384));
        Assert.Equal(5.0, (double)Value(uniform, -1001384)!, 9);
    }

    [Fact]
    public void FramingCutLength_IsLeftOutForCopedEndsAndArcDrivers()
    {
        var coped = Beam([[0, 0, 0], [5, 0, 0]], (1, 0, 0), 0.025);
        var arc = Beam([[0, 0, 0], [5, 0, 0]], null, -1);
        var unknownCoping = Beam([[0, 0, 0], [5, 0, 0]], (1, 0, 0), null);

        ElementBuilder.ApplyFramingCutLength(new RevitFileInfo { Elements = [coped, arc, unknownCoping] });

        Assert.Null(Value(coped, -1001384));
        Assert.Null(Value(arc, -1001384));
        Assert.Null(Value(unknownCoping, -1001384));
    }

    private static TfBlockDeserializer.UnitOptions LengthUnits(string unit, double accuracy, byte suppressTrailingZeros = 0) =>
        new([new("autodesk.spec.aec:length-1.0.0", "", unit, accuracy, 0, 0, 0, suppressTrailingZeros, 0, 0, 0)], 1, 0, 1, 1);

    private static readonly Dictionary<uint, ushort> NoClasses = new();
    private static readonly Dictionary<uint, ushort> GridClasses = new() { [9] = 2108, [374] = 632 };

    private static Element Grid(int id, string name, double x1, double y1, double x2, double y2) =>
        new() { Id = id, CategoryId = -2000220, Name = name, Location = new LocationCurve { CurveType = "Line", StartPoint = [x1, y1, 0], EndPoint = [x2, y2, 0] } };

    private static Element Column(double x, double y, double halfWidth = 0.1) =>
        new()
        {
            Id = 100, CategoryId = -2001330, NativeMasterSymbolId = 5, StructuralConnectionTypes = (-1, -1),
            InstanceOriginXY = (x, y), BoundingBox = [x - halfWidth, y - halfWidth, 0, x + halfWidth, y + halfWidth, 10],
        };

    [Fact]
    public void ColumnLocationMark_NamesTheNearestGridIntersectionWithSignedOffsets()
    {
        var grids = new[] { Grid(1, "A", -50, 0, 50, 0), Grid(2, "B", -50, 30, 50, 30), Grid(3, "3", 10, -50, 10, 50), Grid(4, "4", 20, -50, 20, 50) };
        var offGrid = Column(10, -0.334);   // 101.8 mm south of A, on grid 3
        var onEdge = Column(10, 30.1, 0.1); // grid B along the plan edge: no offset
        foreach (var column in new[] { offGrid, onEdge }) column.Id = column == offGrid ? 100 : 101;

        ElementBuilder.ApplyColumnLocationMarks(new RevitFileInfo { Elements = [.. grids, offGrid, onEdge] },
            LengthUnits("autodesk.unit.unit:millimeters-1.0.1", 1), NoClasses);

        Assert.Equal("A(-102)-3", Value(offGrid, -1002563));
        Assert.Equal("B-3", Value(onEdge, -1002563));
    }

    [Fact]
    public void ColumnLocationMark_FormatsTheOffsetWithTheProjectLengthFormat()
    {
        var grids = new[] { Grid(1, "A", -50, 0, 50, 0), Grid(3, "3", 10, -50, 10, 50) };
        var column = Column(10 + 2275.25 / 304.8, 0, 0.001);

        ElementBuilder.ApplyColumnLocationMarks(new RevitFileInfo { Elements = [.. grids, column] },
            LengthUnits("autodesk.unit.unit:millimeters-1.0.1", 0.01, 1), NoClasses);
        Assert.Equal("A-3(2275.25)", Value(column, -1002563));

        column.Parameters.Clear();
        ElementBuilder.ApplyColumnLocationMarks(new RevitFileInfo { Elements = [.. grids, column] },
            LengthUnits("autodesk.unit.unit:feetFractionalInches-1.0.1", 0.01, 1), NoClasses);
        Assert.Null(Value(column, -1002563));
    }

    [Fact]
    public void ColumnLocationMark_IsLeftOutWhenAGridWasNotReadAsALine()
    {
        var arcGrid = new Element { Id = 9, CategoryId = -2000220, TypeId = 374 };
        var gridType = new Element { Id = 374, CategoryId = -2000220, Name = "6.5mm Bubble" };
        var grids = new[] { Grid(1, "A", -50, 0, 50, 0), Grid(3, "3", 10, -50, 10, 50) };
        var column = Column(10, 0);
        var units = LengthUnits("autodesk.unit.unit:millimeters-1.0.1", 1);

        ElementBuilder.ApplyColumnLocationMarks(new RevitFileInfo { Elements = [.. grids, gridType, column] }, units, GridClasses);
        Assert.Equal("A-3", Value(column, -1002563));

        column.Parameters.Clear();
        ElementBuilder.ApplyColumnLocationMarks(new RevitFileInfo { Elements = [.. grids, gridType, arcGrid, column] }, units, GridClasses);
        Assert.Null(Value(column, -1002563));
    }

    [Fact]
    public void WorkPlane_NamesTheLevelOfTheSketchPlane()
    {
        var names = RvtIO.Lookup.RevitSystemFamilyNames.ForTests(
            new Dictionary<RvtIO.Lookup.SystemFamilyKind, string>(), null,
            new Dictionary<RvtIO.Lookup.RevitUiText, string> { [RvtIO.Lookup.RevitUiText.LevelFamily] = "Level" });
        var level = Level(10, 0, "Top of Parapet");
        var sketchPlane = new Element { Id = 20, SketchPlaneDatumId = 10 };
        var onLevel = new Element { Id = 30, AssocSketchPlaneId = 20 };
        var free = new Element { Id = 31, AssocSketchPlaneId = -1 };
        var onView = new Element { Id = 32, AssocSketchPlaneId = 21 };

        ElementBuilder.ApplyWorkPlaneLabels(new RevitFileInfo { Elements = [level, sketchPlane, new Element { Id = 21, SketchPlaneDatumId = 99 }, onLevel, free, onView] }, names);

        Assert.Equal("Level : Top of Parapet", Value(onLevel, -1001380));
        Assert.Null(Value(free, -1001380));
        Assert.Null(Value(onView, -1001380));
    }

    [Fact]
    public void ColumnLocationMark_TiesGoToTheEarlierGridAndZeroOffsetsAreLeftOut()
    {
        var grids = new[] { Grid(1, "A", -50, 0, 50, 0), Grid(3, "3", 10, -50, 10, 50), Grid(4, "4", 10 + 6400 / 304.8, -50, 10 + 6400 / 304.8, 50) };
        var tie = Column(10 + 3203 / 304.8, 0.0001, 0.001);      // 3203 mm from 3 and 3197 mm from 4: a tie within 6 mm
        tie.BoundingBox = [tie.InstanceOriginXY!.Value.X - 0.001, -0.001, 0, tie.InstanceOriginXY.Value.X + 0.001, 0.001, 10];
        var rounded = Column(10, 0.0004, 0.001);                    // 0.12 mm from A: rounds to 0 and is not written
        rounded.Id = 101;
        rounded.BoundingBox = [9.99, 0.01, 0, 10.01, 0.02, 10];   // the grid does not touch the plan, only the rounded offset is 0

        ElementBuilder.ApplyColumnLocationMarks(new RevitFileInfo { Elements = [.. grids, tie, rounded] },
            LengthUnits("autodesk.unit.unit:millimeters-1.0.1", 1), NoClasses);

        Assert.Equal("A-3(3203)", Value(tie, -1002563));
        Assert.Equal("A-3", Value(rounded, -1002563));
    }

    [Fact]
    public void ColumnLocationMark_IsLeftOutWhenTheOriginIsNotInsideTheColumn()
    {
        var grids = new[] { Grid(1, "A", -50, 0, 50, 0), Grid(3, "3", 10, -50, 10, 50) };
        var nested = Column(0, 0);
        nested.BoundingBox = [30, 5, 0, 31, 6, 10];

        ElementBuilder.ApplyColumnLocationMarks(new RevitFileInfo { Elements = [.. grids, nested] },
            LengthUnits("autodesk.unit.unit:millimeters-1.0.1", 1), NoClasses);

        Assert.Null(Value(nested, -1002563));
    }

    [Fact]
    public void ColumnLocationMark_NeedsStructuralUserData()
    {
        var grids = new[] { Grid(1, "A", -50, 0, 50, 0), Grid(3, "3", 10, -50, 10, 50) };
        var plain = Column(10, 0);
        plain.StructuralConnectionTypes = null;

        ElementBuilder.ApplyColumnLocationMarks(new RevitFileInfo { Elements = [.. grids, plain] },
            LengthUnits("autodesk.unit.unit:millimeters-1.0.1", 1), NoClasses);

        Assert.Null(Value(plain, -1002563));
    }

    [Fact]
    public void ColumnAttachments_FollowTheEndOfTheStoredRecord()
    {
        var top = new Element { Id = 1, CategoryId = -2001330, ColumnAttachment = (true, 0.5, 2) };
        var baseEnd = new Element { Id = 2, CategoryId = -2001330, ColumnAttachment = (false, -0.25, 1) };
        // a field table row filled the top parameters from a record that belongs to the base end
        baseEnd.Parameters.Add(P(-1002557, StorageType.Double, -0.25));
        baseEnd.Parameters.Add(P(-1002555, StorageType.Integer, 1));

        ElementBuilder.ApplyColumnAttachments(new RevitFileInfo { Elements = [top, baseEnd] });

        Assert.Equal(0.5, Value(top, -1002557));
        Assert.Equal(2, Value(top, -1002555));
        Assert.Equal(1, Value(top, -1002559));
        Assert.Null(Value(top, -1002558));
        Assert.Equal(-0.25, Value(baseEnd, -1002558));
        Assert.Equal(1, Value(baseEnd, -1002556));
        Assert.Equal(1, Value(baseEnd, -1002560));
        Assert.Null(Value(baseEnd, -1002557));
        Assert.Null(Value(baseEnd, -1002555));
    }

    [Fact]
    public void FramingCopingDistance_IsAbsentWhileNotCoped()
    {
        var coped = new Element { Id = 1, CategoryId = -2001320, FramingCopingDistance = 0.025 };
        var plain = new Element { Id = 2, CategoryId = -2001320, FramingCopingDistance = -1 };

        ElementBuilder.ApplyFramingCopingDistance(new RevitFileInfo { Elements = [coped, plain] });

        Assert.Equal(0.025, Value(coped, -1001559));
        Assert.Null(Value(plain, -1001559));
    }

    [Fact]
    public void MovesWithGrids_FollowsTheStoredOffsetFlagOfColumnsAndFoundations()
    {
        var column = new Element { Id = 1, CategoryId = -2001330, NativeMasterSymbolId = 5, InstanceUsesOffsetPosition = true };
        var freeColumn = new Element { Id = 2, CategoryId = -2001330, NativeMasterSymbolId = 5, InstanceUsesOffsetPosition = false };
        var footing = new Element { Id = 3, CategoryId = -2001300, NativeMasterSymbolId = 6, InstanceUsesOffsetPosition = true };
        var chair = new Element { Id = 4, CategoryId = -2000080, NativeMasterSymbolId = 7, InstanceUsesOffsetPosition = true };
        var type = new Element { Id = 5, CategoryId = -2001330, InstanceUsesOffsetPosition = true };

        ElementBuilder.ApplyMovesWithGrids(new RevitFileInfo { Elements = [column, freeColumn, footing, chair, type] });

        Assert.Equal(1, Value(column, -1001371));
        Assert.Equal(0, Value(freeColumn, -1001371));
        Assert.Equal(1, Value(footing, -1001371));
        Assert.Null(Value(chair, -1001371));
        Assert.Null(Value(type, -1001371));
    }

    [Fact]
    public void WorkPlane_NamesAReferencePlaneThatIsTheStoredHost()
    {
        var names = RvtIO.Lookup.RevitSystemFamilyNames.ForTests(
            new Dictionary<RvtIO.Lookup.SystemFamilyKind, string>(), null,
            new Dictionary<RvtIO.Lookup.RevitUiText, string>
            {
                [RvtIO.Lookup.RevitUiText.LevelFamily] = "Level",
                [RvtIO.Lookup.RevitUiText.ReferencePlane] = "Reference Plane",
            });
        var plane = new Element { Id = 20, CategoryId = -2000530, Name = "RR_RefPlane" };
        var onPlane = new Element { Id = 30, NativeMasterSymbolId = 5, SerializedHostId = 20 };
        var wall = new Element { Id = 21, CategoryId = -2000011, Name = "Generic - 200mm" };
        var onWall = new Element { Id = 31, NativeMasterSymbolId = 5, SerializedHostId = 21 };
        var level = Level(22, 0, "Level 2");
        var levelPlane = new Element { Id = 23, SketchPlaneDatumId = 22 };
        var onLevelPlane = new Element { Id = 32, NativeMasterSymbolId = 5, SerializedHostId = 23 };

        ElementBuilder.ApplyWorkPlaneLabels(new RevitFileInfo { Elements = [plane, wall, level, levelPlane, onPlane, onWall, onLevelPlane] }, names);
        Assert.Equal("Level : Level 2", Value(onLevelPlane, -1001380));

        Assert.Equal("Reference Plane : RR_RefPlane", Value(onPlane, -1001380));
        Assert.Null(Value(onWall, -1001380));
    }

    [Fact]
    public void HostLabels_AddTheHostOfAnInstanceOnTheFaceOfAnElement()
    {
        var names = RvtIO.Lookup.RevitSystemFamilyNames.ForTests(
            new Dictionary<RvtIO.Lookup.SystemFamilyKind, string>(), null,
            new Dictionary<RvtIO.Lookup.RevitUiText, string> { [RvtIO.Lookup.RevitUiText.LevelFamily] = "Level" });
        var wallType = new Element { Id = 40, Name = "Generic - 200mm", ValidatedSystemFamilyName = "Basic Wall" };
        var wall = new Element { Id = 41, TypeId = 40, CategoryId = -2000011 };
        var onFace = new Element { Id = 50, NativeMasterSymbolId = 5, SerializedHostId = 41, CategoryId = -2000151 };
        onFace.Parameters.Add(P(-1002108, StorageType.ElementId, -1));
        var inWall = new Element { Id = 51, NativeMasterSymbolId = 5, SerializedHostId = 41, CategoryId = -2000023 };
        inWall.Parameters.Add(P(-1002108, StorageType.ElementId, 41));

        ElementBuilder.ApplyHostLabels(new RevitFileInfo { Elements = [wallType, wall, onFace, inWall] }, names, new Dictionary<uint, ushort>());

        Assert.Equal("Basic Wall : Generic - 200mm", Value(onFace, -1001363));
        Assert.Null(Value(inWall, -1001363));
    }

    [Fact]
    public void HostLabels_OnAWorkPlaneTheHostIsTheElementWhoseFaceItLiesOn()
    {
        var names = RvtIO.Lookup.RevitSystemFamilyNames.ForTests(
            new Dictionary<RvtIO.Lookup.SystemFamilyKind, string>(), null,
            new Dictionary<RvtIO.Lookup.RevitUiText, string> { [RvtIO.Lookup.RevitUiText.LevelFamily] = "Level" });
        var floorType = new Element { Id = 40, Name = "Concrete 220", ValidatedSystemFamilyName = "Floor" };
        var floor = new Element { Id = 41, TypeId = 40, CategoryId = -2000032 };
        var level = Level(22, 0, "Level 1");
        var faceOfFloor = new Element { Id = 60, SketchPlaneDatumId = 41 };
        var planeOfLevel = new Element { Id = 61, SketchPlaneDatumId = 22 };
        Element Instance(int id, int plane)
        {
            var e = new Element { Id = id, NativeMasterSymbolId = 5, SerializedHostId = plane, CategoryId = -2001120 };
            e.Parameters.Add(P(-1002108, StorageType.ElementId, -1));
            return e;
        }
        var onFloorFace = Instance(50, 60);
        var onLevelPlane = Instance(51, 61);

        ElementBuilder.ApplyHostLabels(new RevitFileInfo { Elements = [floorType, floor, level, faceOfFloor, planeOfLevel, onFloorFace, onLevelPlane] },
            names, new Dictionary<uint, ushort> { [60] = 3607, [61] = 3607 });

        Assert.Equal("Floor : Concrete 220", Value(onFloorFace, -1001363));
        Assert.Null(Value(onLevelPlane, -1001363));
    }

    [Fact]
    public void PostProcess_DoesNotPublishDiagnosticShellQuantitiesOrFramingExtents()
    {
        var beam = Beam([[0, 0, 0], [5, 0, 0]], (1, 0, 0), -1);
        beam.Id = 1;
        beam.Parameters.Add(P(-1001384, StorageType.Double, 4.0));
        beam.Parameters.Add(P(-1012806, StorageType.Double, 3.0));
        beam.Geometry = SolidQuantitiesTests.Box(0, 0, 0, 5, 1, 1);
        foreach (var face in beam.Geometry.Faces) face.PlaneSurface = true;
        Assert.NotNull(SolidQuantities.OfAllSolids(beam.Geometry));
        var fixture = new Element
        {
            Id = 2, CategoryId = -2001060, NativeMasterSymbolId = 5,
            Geometry = beam.Geometry,
        };
        var state = new ParsingState();
        state.ElemClassIdx[1] = state.ElemClassIdx[2] = 1794;

        ElementBuilder.PostProcess(new RevitFileInfo { Elements = [beam, fixture] }, state);

        Assert.Equal(4.0, Value(beam, -1001384));
        Assert.Equal(3.0, Value(beam, -1012806));
        Assert.Null(Value(fixture, -1012806));
        Assert.Null(Value(fixture, -1012805));
    }

    [Fact]
    public void DiagnosticInstanceQuantities_TakeTheFirstDetailLevelWithSolids()
    {
        var shells = new SolidQuantities.Shell?[]
        {
            null, new SolidQuantities.Shell(2.0, 12.0, 4.0), new SolidQuantities.Shell(5.0, 30.0, 9.0),
        };
        // Coarse has no solids, Medium has: Medium counts. A column has a Volume only.
        var column = new Element { Id = 1, CategoryId = -2001330, NativeMasterSymbolId = 5, LevelSolidCounts = [0, 1, 1], LevelShells = shells };
        // Fixtures have both; the Coarse level wins when it has solids.
        var fixture = new Element
        {
            Id = 2, CategoryId = -2001060, NativeMasterSymbolId = 5, LevelSolidCounts = [2, 2, 3],
            LevelShells = [new SolidQuantities.Shell(1.0, 8.0, 3.0), shells[1], shells[2]],
        };
        var symbol = new Element { Id = 3, CategoryId = -2001330, NativeMasterSymbolId = 5, LevelSolidCounts = [1, 1, 1], LevelShells = shells };
        var coped = new Element
        {
            Id = 4, CategoryId = -2001320, NativeMasterSymbolId = 5, FramingCopingDistance = 0.02, LevelSolidCounts = [0, 1, 1], LevelShells = shells,
        };
        var uncertified = new Element { Id = 5, CategoryId = -2001330, NativeMasterSymbolId = 5, LevelSolidCounts = [1, 1, 1], LevelShells = new SolidQuantities.Shell?[3] };
        var door = new Element { Id = 6, CategoryId = -2000023, NativeMasterSymbolId = 5, LevelSolidCounts = [1, 1, 1], LevelShells = shells };
        var unmeasured = new Element { Id = 7, CategoryId = -2001330, NativeMasterSymbolId = 5 };
        var classes = new Dictionary<uint, ushort> { [1] = 1794, [2] = 1794, [3] = 1829, [4] = 1794, [5] = 1794, [6] = 1794, [7] = 1794 };

        ElementBuilder.ApplyInstanceQuantities(new RevitFileInfo { Elements = [column, fixture, symbol, coped, uncertified, door, unmeasured] }, classes);

        Assert.Equal(2.0, Value(column, -1012806));
        Assert.Null(Value(column, -1012805));
        Assert.Equal(1.0, Value(fixture, -1012806));
        Assert.Equal(4.0, Value(fixture, -1012805));
        Assert.Null(Value(symbol, -1012806));
        Assert.Null(Value(coped, -1012806));
        Assert.Null(Value(uncertified, -1012806));
        Assert.Null(Value(door, -1012806));
        Assert.Null(unmeasured.LevelSolidCounts);
        Assert.Null(unmeasured.LevelShells);
        Assert.Null(Value(unmeasured, -1012806));
    }

    [Fact]
    public void ApplyInstanceQuantities_ArchitecturalColumnAndPlumbing_SetsVolume()
    {
        SolidQuantities.Shell?[] shells =
        [
            null,
            new SolidQuantities.Shell(3.5, 12.0, 6.0),
            new SolidQuantities.Shell(4.0, 14.0, 7.0),
        ];

        var archCol = new Element { Id = 10, CategoryId = -2000100, NativeMasterSymbolId = 5, LevelSolidCounts = [0, 1, 1], LevelShells = shells };
        var plumbing = new Element { Id = 11, CategoryId = -2001040, NativeMasterSymbolId = 5, LevelSolidCounts = [0, 1, 1], LevelShells = shells };
        var classes = new Dictionary<uint, ushort> { [10] = 1794, [11] = 1794 };

        ElementBuilder.ApplyInstanceQuantities(new RevitFileInfo { Elements = [archCol, plumbing] }, classes);

        Assert.Equal(3.5, Value(archCol, -1012806));
        Assert.Null(Value(archCol, -1012805));
        Assert.Equal(3.5, Value(plumbing, -1012806));
        Assert.Null(Value(plumbing, -1012805));
    }
}
