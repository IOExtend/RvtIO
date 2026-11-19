using System.Text;
using System.Text.RegularExpressions;
using RvtIO.Ifc;
using RvtIO.Model;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

/// <summary>The centre lines of a shape driven bar (<see cref="RebarReader"/>) read from a hand-made Rebar block in the grammar of
/// <see cref="WideBlockWalker"/>, and the pieces that read them: the keyed template field and the byte array capture of the walker.</summary>
public class RebarReaderTests
{
    private const int Identifier = 1, ElementId = 2, Trf = 3, GCurve = 4, GLine = 5, GArc = 6, OneBar = 7, Cache = 8, InstBase = 9,
        Instance = 10, Element = 11, Rebar = 12;

    private static SchemaField Field(string name, byte type, byte mods = 0, int? reference = null, int? fixedCount = null,
        List<SchemaField>? args = null) => new(name, type, mods, 0, fixedCount, reference, null, args);

    private static SchemaDocument Schema(bool freeForm = false) => new()
    {
        Classes =
        [
            new(Identifier, "Identifier", 0, 2, [Field("m_id64", 11)], []),
            new(ElementId, "ElementId", 0, 1, [Field("m_id", 14, 0, Identifier)], []),
            new(Trf, "Trf", 0, 1, [Field("m_3x3", 13, 16, null, 3, [Field("", 7, 16, null, 3)]), Field("m_or", 7, 16, null, 3)], []),
            new(GCurve, "GCurve", 0, 1, [Field("m_endParams", 7, 16, null, 2)], []),
            new(GLine, "GLine", GCurve, 1, [Field("m_origin", 7, 16, null, 3), Field("m_dirVec", 7, 16, null, 3)], []),
            new(GArc, "GArc", GCurve, 1,
                [Field("m_xVec", 7, 16, null, 3), Field("m_yVec", 7, 16, null, 3), Field("m_radius", 7), Field("m_center", 7, 16, null, 3)], []),
            new(OneBar, "OneBarCenterlineCurves", 0, 1, [Field("m_centerlineCurves", 14, 81)], []),
            new(Cache, "RebarInstCache", 0, 1,
                [Field("m_noSelfIntersectingCenterlineCurves", 14, 81), Field("m_selfIntersectingCenterlineCurves", 14, 81)], []),
            new(InstBase, "RebarInstBase", 0, 1,
                [Field("m_barPositionsRelativeToFirstBarArr", 14, 80, Trf), Field("m_oCacheData", 14, 1), Field("m_includedBars", 1, 80),
                 Field("m_barTypeId", 14, 0, ElementId), Field("m_barLength", 7),
                 .. (freeForm ? new[] { Field("m_numberOfBarPositions", 4) } : [])], []),
            new(Instance, freeForm ? "RebarInstFreeForm" : "RebarInstance", InstBase, 1, [], []),
            new(Element, "Element", 0, 1, [Field("m_id", 14, 0, ElementId)], []),
            new(Rebar, "Rebar", Element, 1, [Field("m_oInstance", 14, 1)], []),
        ],
    };

    private static void Pointer(BinaryWriter w, int tag) { w.Write(1u); w.Write((ushort)tag); }

    private static void Transform(BinaryWriter w, double x)
    {
        foreach (double v in new[] { 1.0, 0, 0, 0, 1, 0, 0, 0, 1 }) w.Write(v);   // m_3x3 (identity)
        w.Write(x); w.Write(0.0); w.Write(0.0);                                   // m_or
    }

    [Fact]
    public void KeyedTemplateField_IsCapturedPerRowAndColumn()
    {
        var schema = Schema();
        var stream = new MemoryStream();
        var w = new BinaryWriter(stream, Encoding.Unicode);
        w.Write((ushort)Trf);
        foreach (double v in new[] { 1.0, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 }) w.Write(v);
        var block = stream.ToArray();
        var walk = new WideBlockWalker(schema, RebarReader.Key, []).Run(block, 0, block.Length, Trf);
        Assert.True(walk.Exact, walk.Error);
        Assert.Equal(6, walk.Captures.Single(c => c.Key == "Trf.m_3x3@1/2").Real);
        Assert.Equal(7, walk.Captures.Single(c => c.Key == "Trf.m_3x3@2/0").Real);
        Assert.Equal(12, walk.Captures.Single(c => c.Key == "Trf.m_or@2").Real);
    }

    [Fact]
    public void SegmentMoved_AppliesRotationThenOrigin()
    {
        // a quarter turn about Z followed by a shift of (10, 0, 0)
        double[] trf = [0, -1, 0, 1, 0, 0, 0, 0, 1, 10, 0, 0];
        var line = new RebarSegment(false, [1, 0, 0], [1, 0, 0], [0, 0, 0], 0, 0, 2);
        var moved = line.Moved(trf);
        Assert.Equal([10, 1, 0], moved.Origin.Select(v => Math.Round(v)).ToArray());
        Assert.Equal([0, 1, 0], moved.Axis.Select(v => Math.Round(v)).ToArray());
        var arc = new RebarSegment(true, [1, 0, 0], [1, 0, 0], [0, 1, 0], 0.5, 0, Math.PI / 2).Moved(trf);
        Assert.Equal([0, 1, 0], arc.Axis.Select(v => Math.Round(v)).ToArray());
        Assert.Equal([-1, 0, 0], arc.Axis2.Select(v => Math.Round(v)).ToArray());
        Assert.Equal(0.5, arc.Radius);
    }

    /// <summary>A Rebar of two bar positions (offsets 0 and 1 foot) whose first bar is a 2 foot line and a quarter circle.</summary>
    private static byte[] RebarBlockWithCurves(bool included)
    {
        var stream = new MemoryStream();
        var w = new BinaryWriter(stream, Encoding.Unicode);
        w.Write((ushort)Rebar);
        w.Write(55L);
        Pointer(w, Instance);
        w.Write(2u); Transform(w, 0); Transform(w, 1);
        Pointer(w, Cache);
        w.Write(2u); w.Write((byte)1); w.Write((byte)(included ? 1 : 0));
        w.Write(9L);
        w.Write(3.5);
        w.Write(1u); Pointer(w, OneBar);
        w.Write(0u);
        w.Write(2u); Pointer(w, GLine); Pointer(w, GArc);
        // q3 GLine: params 0..2, origin (0,0,0), direction (1,0,0)
        foreach (double v in new[] { 0.0, 2.0, 0, 0, 0, 1, 0, 0 }) w.Write(v);
        // q4 GArc: params 0..pi/2, xVec (1,0,0), yVec (0,1,0), radius 0.5, centre (2,0.5,0)
        foreach (double v in new[] { 0.0, Math.PI / 2, 1, 0, 0, 0, 1, 0, 0.5, 2, 0.5, 0 }) w.Write(v);
        return stream.ToArray();
    }

    [Fact]
    public void ReadRebar_MovesTheCachedBarByEveryPositionTwiceAndKeepsTheLengthAndType()
    {
        var walker = new WideBlockWalker(Schema(), RebarReader.Key, []);
        var block = RebarBlockWithCurves(included: true);
        var walk = walker.Run(block, 0, block.Length, Rebar);
        Assert.True(walk.Exact, walk.Error);
        var bars = RebarReader.ReadRebar(walker, walk);
        Assert.NotNull(bars);
        Assert.Equal(9, bars.BarTypeId);
        Assert.Equal(3.5, bars.BarLength);
        Assert.Equal(2, bars.Bars.Count);
        Assert.Equal(2, bars.Bars[0].Count);
        Assert.Equal(0, bars.Bars[0][0].Origin[0]);
        // The current profile applies the position transform twice: the second position (offset 1) lies 2 feet from the first
        Assert.Equal(2, bars.Bars[1][0].Origin[0]);
        Assert.Equal(1, bars.ModelPaths![1][0].Origin[0]);
        Assert.Equal(4, bars.Bars[1][1].Origin[0]);
        Assert.True(bars.Bars[0][1].IsArc);
        Assert.Equal(0.5, bars.Bars[0][1].Radius);
    }

    [Fact]
    public void ReadRebar_LeavesOutABarThatIsNotIncluded()
    {
        var walker = new WideBlockWalker(Schema(), (d, f) => RebarReader.Key(d, f), []);
        var block = RebarBlockWithCurves(included: false);
        var walk = walker.Run(block, 0, block.Length, Rebar);
        Assert.True(walk.Exact, walk.Error);
        Assert.Single(RebarReader.ReadRebar(walker, walk)!.Bars);
    }


    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadFreeFormRebarKeepsTheCachedPathOfEachIncludedPosition(bool secondIncluded)
    {
        var schema = Schema(freeForm: true);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.Unicode);
        writer.Write((ushort)Rebar);
        writer.Write(55L);
        Pointer(writer, Instance);
        writer.Write(2u);
        Transform(writer, 0);
        Transform(writer, 100);
        Pointer(writer, Cache);
        writer.Write(2u);
        writer.Write((byte)1);
        writer.Write((byte)(secondIncluded ? 1 : 0));
        writer.Write(9L);
        writer.Write(3.5);
        writer.Write(2);
        writer.Write(2u);
        Pointer(writer, OneBar);
        Pointer(writer, OneBar);
        writer.Write(0u);
        writer.Write(1u);
        Pointer(writer, GLine);
        writer.Write(1u);
        Pointer(writer, GLine);
        foreach (double value in new[] { 0.0, 2, 1, 0, 0, 1, 0, 0, 0, 2, 7, 0, 0, 1, 0, 0 })
            writer.Write(value);
        var data = stream.ToArray();
        var walker = new WideBlockWalker(schema, RebarReader.Key, []);
        var walk = walker.Run(data, 0, data.Length, Rebar);
        Assert.True(walk.Exact, walk.Error);
        var bars = RebarReader.ReadRebar(walker, walk);
        Assert.NotNull(bars);
        Assert.True(bars.IsFreeForm);
        Assert.Equal(secondIncluded ? new[] { 1.0, 7.0 } : new[] { 1.0 }, bars.Bars.Select(path => path[0].Origin[0]));
    }

    private static string Export(params Element[] elements)
    {
        var info = new RevitFileInfo();
        info.Elements.AddRange(elements);
        string path = Path.Combine(Path.GetTempPath(), $"revitreader-rebar-{Guid.NewGuid():N}.ifc");
        try
        {
            new IfcExporter { Profile = IfcProfile.Current }.Export(info, path);
            return File.ReadAllText(path);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    /// <summary>One bar of a line (1 foot) and a quarter circle (radius 0.5 foot), in a system.</summary>
    internal static Element Bar(int id, int systemId, bool inAssembly = false, double z = 0, int? hostId = null)
    {
        IReadOnlyList<RebarSegment> bar =
        [
            new(false, [0, 0, z], [1, 0, 0], [0, 0, 0], 0, 0, 1),
            new(true, [1, 0.5, z], [0, -1, 0], [1, 0, 0], 0.5, 0, Math.PI / 2),
        ];
        // diameters 8 mm (nominal) and 8 mm (model), in feet; the document rounds lengths to 10 mm
        return new Element
        {
            Id = id, CategoryId = -2009000, TypeId = 900, HostId = hostId,
            RebarBars = new RebarBars(900, systemId, null, [bar, bar], 8 / 304.8, 8 / 304.8, "Rebar 8", 10, InAssembly: inAssembly),
        };
    }

    [Fact]
    public void Export_WritesAReinforcingBarWithOneSweptDiskPerBarAndAGroupForItsSystem()
    {
        string ifc = Export(Bar(500, 600), Bar(501, 600), new Element { Id = 600, CategoryId = -2009003, Name = "Area Reinforcement 1" });
        string[] lines = ifc.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        string bar = Assert.Single(lines, l => l.Contains("IFCREINFORCINGBAR(") && l.Contains("'500'"));
        // Name family:type:id, ObjectType the type name for a bar of a system, Tag, SteelGrade, diameter, area, rounded length (1 ft + 0.5 ft * pi / 2 = 544.2 mm)
        Assert.Contains("'Rebar Bar:Rebar 8:500','','Rebar 8',", bar);
        Assert.Contains(",'500','',8.,", bar);
        Assert.Contains(",540.,.NOTDEFINED.,$)", bar);
        Assert.Equal(4, lines.Count(l => l.Contains("IFCSWEPTDISKSOLID(")));
        // disk radius 4 mm, end parameter 304.8 mm of line + 90 degrees of arc
        Assert.Contains(lines, l => l.Contains("IFCSWEPTDISKSOLID(") && l.Contains(",4.,$,0.,394.8)"));
        Assert.Contains(lines, l => l.Contains("IFCTRIMMEDCURVE(") && l.Contains("IFCPARAMETERVALUE(90.)"));
        Assert.Contains(lines, l => l.Contains("IFCCIRCLE(") && l.EndsWith(",152.4);"));
        // the group of the system relates both bars
        Assert.Contains(lines, l => l.Contains("IFCGROUP(") && l.Contains("'Structural Area Reinforcement:Area Reinforcement 1'"));
        var relation = Assert.Single(lines, l => l.Contains("IFCRELASSIGNSTOGROUP("));
        Assert.Matches(@",\(#\d+,#\d+\),\$,#\d+\);", relation);
        // a project without a storey contains its bars in the building
        string id = bar[..bar.IndexOf('=')];
        var containment = Assert.Single(lines, l => l.Contains("IFCRELCONTAINEDINSPATIALSTRUCTURE("));
        Assert.Contains(id, Regex.Match(containment, @",\(([^)]*)\),").Groups[1].Value.Split(','));
        Assert.Contains(lines, l => l.StartsWith("#") && l.Contains("=IFCBUILDING(") && containment.EndsWith(l[..l.IndexOf('=')] + ");"));
    }

    [Fact]
    public void Export_GivesTheOneBarOfASystemTheGlobalIdOfTheSystem()
    {
        Guid alone = Guid.NewGuid(), aloneSystem = Guid.NewGuid(), member = Guid.NewGuid();
        var only = Bar(500, 600);
        only.SerializedExportId = alone;
        only.RebarBars = only.RebarBars! with { OnlyBarOfSystem = true };
        var other = Bar(501, 601);
        other.SerializedExportId = member;
        string ifc = Export(only, other,
            new Element { Id = 600, CategoryId = -2009003, Name = "Area Reinforcement 1", SerializedExportId = aloneSystem },
            new Element { Id = 601, CategoryId = -2009003, Name = "Area Reinforcement 2", SerializedExportId = Guid.NewGuid() });
        string[] lines = ifc.Split('\n');

        // the one RebarInSystem of its system takes the GlobalId of the system; a bar of a system with more takes its own sub-element (bars + 3)
        Assert.Contains(lines, l => l.Contains("IFCREINFORCINGBAR('" + IfcGuid.ToIfcGuid(aloneSystem) + "'") && l.Contains("'500'"));
        Assert.Contains(lines, l => l.Contains("IFCREINFORCINGBAR('" + IfcGuid.ToIfcGuid(ElementExportIds.SubElementId(member, 5)) + "'") && l.Contains("'501'"));
        // the system whose GlobalId the bar carries is written as no group; the other one is
        Assert.DoesNotContain(lines, l => l.Contains("IFCGROUP(") && l.Contains("Area Reinforcement 1"));
        Assert.Contains(lines, l => l.Contains("IFCGROUP(") && l.Contains("Area Reinforcement 2"));
    }

    /// <summary>A container of two items (one bar each) of the container type "Container 1".</summary>
    private static Element Container(int id)
    {
        IReadOnlyList<RebarSegment> line = [new(false, [0, 0, 0], [1, 0, 0], [0, 0, 0], 0, 0, 1)];
        RebarBars Item(int index) => new(900, 0, null, [line], 8 / 304.8, 8 / 304.8, "Rebar 8", 10, ItemIndex: index);
        return new Element { Id = id, CategoryId = -2009000, RebarBars = new RebarBars(-1, 0, null, [], BarTypeName: "Container 1", Items: [Item(0), Item(1)]) };
    }

    [Fact]
    public void Export_WritesEachItemOfARebarContainerAsABarAndGroupsThemUnderTheContainer()
    {
        string[] lines = Export(Container(700)).Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        var bars = lines.Where(l => l.Contains("IFCREINFORCINGBAR(")).ToArray();
        Assert.Equal(2, bars.Length);
        // name family:container type:container id: item index, ObjectType family:container type, Tag the container id, the bar's own diameter and length
        Assert.Contains("'Structural Rebar Container:Container 1:700: 0','','Structural Rebar Container:Container 1',", bars[0]);
        Assert.Contains("'Structural Rebar Container:Container 1:700: 1','','Structural Rebar Container:Container 1',", bars[1]);
        Assert.All(bars, bar => Assert.Contains(",'700','',8.,", bar));
        Assert.All(bars, bar => Assert.Contains(",300.,.NOTDEFINED.,$)", bar));
        // one group named by the container type holds both, one Pset_QuantityTakeOff (Reference = the container type) serves both
        Assert.Single(lines, l => l.Contains("IFCGROUP(") && l.Contains("'Structural Rebar Container:Container 1'"));
        var assignment = Assert.Single(lines, l => l.Contains("IFCRELASSIGNSTOGROUP("));
        Assert.Matches(@",\(#\d+,#\d+\),\$,#\d+\);", assignment);
        Assert.Single(lines, l => l.Contains("IFCPROPERTYSET(") && l.Contains("'Pset_QuantityTakeOff'"));
        Assert.Contains(lines, l => l.Contains("IFCPROPERTYSINGLEVALUE(") && l.Contains("'Reference'") && l.Contains("IFCIDENTIFIER('Container 1')"));
        // both bars are contained once
        var containment = Assert.Single(lines, l => l.Contains("IFCRELCONTAINEDINSPATIALSTRUCTURE("));
        Assert.Equal(2, Regex.Match(containment, @",\(([^)]*)\),").Groups[1].Value.Split(',').Length);
    }

    [Fact]
    public void Export_GivesTheItemsAndTheGroupOfAContainerWithoutAnExportIdDistinctGlobalIds()
    {
        string[] lines = Export(Container(700), Container(701)).Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        var ids = lines.Where(l => l.Contains("IFCREINFORCINGBAR(") || l.Contains("IFCGROUP(")).Select(l => Regex.Match(l, @"\('([^']*)'").Groups[1].Value).ToArray();
        // 2 containers: 2 items and a group each
        Assert.Equal(6, ids.Length);
        Assert.All(ids, id => Assert.Equal(22, id.Length));
        Assert.Equal(ids.Length, ids.Distinct().Count());
    }

    [Fact]
    public void Export_CountsEveryWrittenReinforcingBarAsAnExportedProduct()
    {
        var info = new RevitFileInfo();
        info.Elements.AddRange([Bar(500, 600), Bar(501, 600), new Element { Id = 600, CategoryId = -2009003, Name = "Area Reinforcement 1" }]);
        string path = Path.Combine(Path.GetTempPath(), $"rvtio-rebar-{Guid.NewGuid():N}.ifc");
        var skipLog = new SkipLog();
        var exporter = new IfcExporter { Profile = IfcProfile.Current, SkipLog = skipLog };
        try
        {
            exporter.Export(info, path);
            int bars = File.ReadAllText(path).Split('\n').Count(l => l.Contains("IFCREINFORCINGBAR("));
            Assert.Equal(2, bars);
            Assert.Equal(bars, exporter.ExportedProducts);
            Assert.Equal(info.Elements.Count, exporter.ExportedProducts + skipLog.Total);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Export_ContainsTheBarsOfAnAssemblyThatIsNotWrittenAndWritesNoGroupForThem()
    {
        string ifc = Export(Bar(500, 600, inAssembly: true), Bar(501, 600, inAssembly: true), new Element { Id = 600, CategoryId = -2009003, Name = "Area Reinforcement 1" });
        Assert.Equal(2, ifc.Split('\n').Count(l => l.Contains("IFCREINFORCINGBAR(")));
        Assert.DoesNotContain("IFCGROUP(", ifc);
        Assert.DoesNotContain("IFCRELASSIGNSTOGROUP(", ifc);
        // no assembly is written, so nothing aggregates the members: each is contained in the building like a bar that is not a member
        var containment = Assert.Single(ifc.Split('\n'), l => l.Contains("IFCRELCONTAINEDINSPATIALSTRUCTURE("));
        Assert.Equal(2, Regex.Match(containment, @",\(([^)]*)\),").Groups[1].Value.Split(',').Length);
    }

    private static Element Level(int id, string name, double elevationFeet) => new()
    {
        Id = id, Name = name, CategoryId = -2000240, Category = "OST_Levels", IsNativeLevel = true,
        SerializedFamilyId = -1, SerializedUnplacedOwnerId = -1, SerializedOwnerDbViewId = -1,
        Parameters =
        [
            new Parameter { Id = -1007102, Definition = new Definition { Name = "LEVEL_ELEV" }, StorageType = StorageType.Double, Value = elevationFeet },
            new Parameter { Id = -1007111, StorageType = StorageType.Integer, Value = 1 },
        ],
    };

    /// <summary>Levels at 0, 10 and 10.02 feet; a Part whose box starts at 9.8 feet hosts (through a held instance) a bar that lies at 20 feet.</summary>
    [Theory]
    [InlineData(true, "Level A")]
    [InlineData(false, "Level C")]
    public void Export_ContainsTheBarOfAnUnwrittenAssemblyInTheStoreyOfItsHostPart(bool inAssembly, string storey)
    {
        string ifc = Export(Level(10, "Level A", 0), Level(20, "Level B", 10), Level(30, "Level C", 10.02),
            new Element { Id = 700, CategoryId = -2000269, BoundingBox = [0, 0, 9.8, 1, 1, 12] },
            new Element { Id = 701, HostId = 700 },
            Bar(500, 0, inAssembly: inAssembly, z: 20, hostId: 701));
        string[] lines = ifc.Split('\n');
        string bar = Assert.Single(lines, l => l.Contains("IFCREINFORCINGBAR("));
        string barId = bar[..bar.IndexOf('=')];
        string storeyLine = Assert.Single(lines, l => l.Contains("=IFCBUILDINGSTOREY(") && l.Contains($"'{storey}'"));
        string storeyId = storeyLine[..storeyLine.IndexOf('=')];
        var containment = Assert.Single(lines, l => l.Contains("IFCRELCONTAINEDINSPATIALSTRUCTURE(") && Regex.IsMatch(l, $@"\(([^)]*,)?{barId}(,[^)]*)?\)"));
        Assert.EndsWith($",{storeyId});", containment.TrimEnd('\r'));
    }

    [Fact]
    public void SegmentMinZ_IsTheLowestPointOfALineAndOfAnArc()
    {
        Assert.Equal(1, new RebarSegment(false, [0, 0, 3], [0, 0, 1], [0, 0, 0], 0, -2, 2).MinZ());
        // a half circle in the XZ plane through its lowest point: centre z 10, radius 2, angles 180 to 360 degrees (z = 10 + 2 sin a)
        Assert.Equal(8, new RebarSegment(true, [0, 0, 10], [1, 0, 0], [0, 0, 1], 2, Math.PI, 2 * Math.PI).MinZ(), 12);
        // the quarter from 0 to 90 degrees stays above the centre: its lowest point is the start
        Assert.Equal(10, new RebarSegment(true, [0, 0, 10], [1, 0, 0], [0, 0, 1], 2, 0, Math.PI / 2).MinZ(), 12);
        // the lowest point of the circle at 270 degrees is also found when the arc is given by a start beyond one turn
        Assert.Equal(8, new RebarSegment(true, [0, 0, 10], [1, 0, 0], [0, 0, 1], 2, 2 * Math.PI + 3.5, 2 * Math.PI + 5.5).MinZ(), 12);
    }

    private static IReadOnlyList<RebarSegment> Line(double x, double z, double length) => [new(false, [x, 0, z], [1, 0, 0], [0, 0, 0], 0, 0, length)];

    /// <summary>Twelve lines of 10 feet one foot apart (z 0 to 11, bars of radius 0.1) and a window between x = 4 and x = 6 over the lines 6 to 8.</summary>
    [Fact]
    public void CutBars_TakesTheLinesWhoseMovedFirstBarLiesInTheOutline()
    {
        var lines = Enumerable.Range(0, 12).Select(k => Line(0, k, 10)).ToList();
        const double r = 0.1;
        // whole bars of the lines 0 to 5, and the piece left of the window (x 0 to 4) of the lines 6 to 8
        var whole = RebarReader.CutBars(lines, new RebarReader.CutInstance(Line(0, 0, 10)[0], [-r, -r, -r], [10 + r, r, 5 + r]), r)!;
        Assert.Equal(6, whole.Count);
        Assert.Equal(Enumerable.Range(0, 6).Select(k => (double)k), whole.Select(bar => bar[0].Origin[2]));
        var left = RebarReader.CutBars(lines, new RebarReader.CutInstance(Line(0, 6, 4)[0], [-r, -r, 6 - r], [4 + r, r, 8 + r]), r)!;
        Assert.Equal([6.0, 7.0, 8.0], left.Select(bar => bar[0].Origin[2]));
        Assert.All(left, bar => Assert.Equal(4, bar[0].End));
        // the piece right of the window starts on its own x and still lies on the lines 6 to 8
        var right = RebarReader.CutBars(lines, new RebarReader.CutInstance(Line(6, 6, 4)[0], [6 - r, -r, 6 - r], [10 + r, r, 8 + r]), r)!;
        Assert.Equal([6.0, 7.0, 8.0], right.Select(bar => bar[0].Origin[2]));
        Assert.All(right, bar => Assert.Equal(6, bar[0].Origin[0]));
    }

    [Fact]
    public void CutBars_IsNullWhenTheFirstBarLiesOnNoLine()
    {
        var lines = Enumerable.Range(0, 3).Select(k => Line(0, k, 10)).ToList();
        Assert.Null(RebarReader.CutBars(lines, new RebarReader.CutInstance(Line(0, 0.5, 10)[0], [-1, -1, -1], [11, 1, 4]), 0.1));
    }
}
