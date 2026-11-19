using RvtIO.Ifc;
using RvtIO.Model;
using System.Text.RegularExpressions;
using Xunit;

namespace RvtIO.Tests;

/// <summary>
/// IfcSpace the way the current profile writes it: contained in its storey (never aggregated), Description from the Comments
/// parameter, empty ObjectType, zero ElevationWithFlooring, rooms only of the last project phase that is a phase
/// element, no locationless rooms, and an IfcGroup per area scheme. The legacy profile form is pinned by IfcExporterSpacesTests.
/// </summary>
public class IfcExporterSpacesCurrentTests
{
    private const int RoomCategoryId = -2000160;
    private const int AreaCategoryId = -2003200;
    private const int PhaseCategoryId = -2000112;
    private const int AreaSchemeCategoryId = -2003201;

    [Fact]
    public void Space_IsContainedInItsStoreyAndWritesTheCurrentAttributes()
    {
        string ifc = Export(new RevitFileInfo { Elements = [Level(900), Room(100, "Living Room", "1.20", comments: "note")] });
        int spaceId = Ids(ifc, "IFCSPACE").Single();
        var args = Args(ifc, spaceId, "IFCSPACE");
        Assert.Equal("'1.20'", args[2]);
        Assert.Equal("'note'", args[3]);   // Description = Comments
        Assert.Equal("''", args[4]);       // ObjectType
        Assert.Equal("'Living Room'", args[7]);
        Assert.Equal("0.", args[10]);      // ElevationWithFlooring

        int storeyId = Ids(ifc, "IFCBUILDINGSTOREY").Single();
        var relation = Ids(ifc, "IFCRELCONTAINEDINSPATIALSTRUCTURE").Select(id => Args(ifc, id, "IFCRELCONTAINEDINSPATIALSTRUCTURE"))
            .Single(a => a[5] == $"#{storeyId}");
        Assert.Equal($"(#{spaceId})", relation[4]);
        foreach (int aggregate in Ids(ifc, "IFCRELAGGREGATES"))
            Assert.DoesNotContain($"#{spaceId}", Args(ifc, aggregate, "IFCRELAGGREGATES")[5]);
    }

    [Fact]
    public void SpaceWithoutComments_HasAnEmptyDescription()
    {
        string ifc = Export(new RevitFileInfo { Elements = [Level(900), Room(100, "Hall", "2", comments: "")] });
        Assert.Equal("''", Args(ifc, Ids(ifc, "IFCSPACE").Single(), "IFCSPACE")[3]);
    }

    [Fact]
    public void PlacedRoomWithAnExplicitlyEmptyNumber_UsesItsNameAndSourceId()
    {
        var placed = Room(100, "Vestibule", "");
        placed.SerializedRoomIsLocationless = false;
        placed.NativeId = (long)int.MaxValue + 1;
        var missing = Room(200, "Missing", "");
        missing.SerializedRoomIsLocationless = false;
        missing.Parameters.RemoveAll(p => p.Id == -1006901);
        var unverified = Room(300, "Unverified", "");
        var unplaced = Room(400, "Unplaced", "");
        unplaced.SerializedRoomIsLocationless = true;
        var info = new RevitFileInfo { Elements = [Level(900), placed, missing, unverified, unplaced] };

        string ifc = Export(info);

        Assert.Equal("'Vestibule:2147483648'", Args(ifc, Ids(ifc, "IFCSPACE").Single(), "IFCSPACE")[2]);
        Assert.Single(Ids(ifc, "IFCSPACETYPE"));
        Assert.Equal("'Vestibule:100'", Args(ifc, Ids(ifc, "IFCSPACETYPE").Single(), "IFCSPACETYPE")[2]);
        Assert.Empty(Ids(Export(info, new IfcExporter { Profile = IfcProfile.Legacy }), "IFCSPACE"));
    }

    [Fact]
    public void Spaces_ShareTheContainmentOfTheirStorey_InFrontOfItsProducts()
    {
        string ifc = Export(new RevitFileInfo { Elements = [Level(900), Room(100, "A", "1"), Room(200, "B", "2")] });
        int storeyId = Ids(ifc, "IFCBUILDINGSTOREY").Single();
        var relations = Ids(ifc, "IFCRELCONTAINEDINSPATIALSTRUCTURE").Select(id => Args(ifc, id, "IFCRELCONTAINEDINSPATIALSTRUCTURE"))
            .Where(a => a[5] == $"#{storeyId}").ToList();
        Assert.Single(relations);
        Assert.Equal(2, Regex.Matches(relations[0][4], @"#\d+").Count);
    }

    [Theory]
    [InlineData(7, new[] { 7, 31 }, 0)]       // the room is in the first phase, the last one is 31
    [InlineData(31, new[] { 7, 31 }, 1)]
    [InlineData(7, new[] { 7, 31, 99 }, 0)]    // 99 is no phase element: the last real phase is 31
    [InlineData(31, new[] { 7, 31, 99 }, 1)]
    public void Room_IsExportedOnlyInTheLastPhaseThatExists(int roomPhase, int[] order, int expected)
    {
        var room = Room(100, "A", "1");
        room.SerializedRoomPhaseId = roomPhase;
        var info = new RevitFileInfo { Elements = [Level(900), room, Phase(7), Phase(31)], SerializedProjectPhaseIds = order };
        Assert.Equal(expected, Ids(Export(info), "IFCSPACE").Count);
    }

    [Fact]
    public void RoomPhase_NoFilterWithoutPhaseSource()
    {
        var room = Room(100, "A", "1");
        room.SerializedRoomPhaseId = 7;
        // No validated order, an order without phase elements, a discovered-only phase element: every room stays.
        Assert.Single(Ids(Export(new RevitFileInfo { Elements = [Level(900), room] }), "IFCSPACE"));
        Assert.Single(Ids(Export(new RevitFileInfo { Elements = [Level(900), room], SerializedProjectPhaseIds = [7, 31] }), "IFCSPACE"));
    }

    [Fact]
    public void RoomPhase_AllPhasesOptionAndExplicitPhaseOverrideTheDefault()
    {
        var room = Room(100, "A", "1");
        room.SerializedRoomPhaseId = 7;
        var info = new RevitFileInfo { Elements = [Level(900), room, Phase(7), Phase(31)], SerializedProjectPhaseIds = [7, 31] };
        Assert.Empty(Ids(Export(info), "IFCSPACE"));
        Assert.Single(Ids(Export(info, new IfcExporter { ExportRoomsOfAllPhases = true }), "IFCSPACE"));
        Assert.Single(Ids(Export(info, new IfcExporter { RoomExportPhaseId = 7 }), "IFCSPACE"));
    }

    [Fact]
    public void RoomPhase_AreasAreNeverFilteredByPhase()
    {
        var area = Room(100, "Office", "1", categoryId: AreaCategoryId);
        area.SerializedRoomPhaseId = 7;
        var info = new RevitFileInfo { Elements = [Level(900), area, Phase(7), Phase(31)], SerializedProjectPhaseIds = [7, 31] };
        Assert.Single(Ids(Export(info), "IFCSPACE"));
    }

    [Fact]
    public void LocationlessRoom_IsNotExported_ButAnAreaIs()
    {
        var room = Room(100, "A", "1");
        room.SerializedRoomIsLocationless = true;
        var area = Room(200, "B", "2", categoryId: AreaCategoryId);
        area.SerializedRoomIsLocationless = true;
        var placed = Room(300, "C", "3");
        placed.SerializedRoomIsLocationless = false;
        string ifc = Export(new RevitFileInfo { Elements = [Level(900), room, area, placed] });
        Assert.Equal(["'2'", "'3'"], Ids(ifc, "IFCSPACE").Select(id => Args(ifc, id, "IFCSPACE")[2]).Order().ToArray());
    }

    [Fact]
    public void SpaceOnALevelThatIsNotAStorey_StandsInTheHighestStoreyAtOrBelow_AndIsNotWrittenBelowTheLowest()
    {
        string ifc = Export(new RevitFileInfo
        {
            Elements =
            [
                Level(900, "Ground"), Level(901, "First", elevation: 10),
                Level(902, "Landing", elevation: 12, buildingStory: false), Level(903, "Pit", elevation: -3, buildingStory: false),
                Room(100, "Stair", "1", levelId: 902), Room(200, "Sump", "2", levelId: 903), Room(300, "Hall", "3"),
            ],
        });

        var spaces = Ids(ifc, "IFCSPACE").ToDictionary(id => Args(ifc, id, "IFCSPACE")[2]);
        Assert.Equal(["'1'", "'3'"], spaces.Keys.Order().ToArray());
        int first = Ids(ifc, "IFCBUILDINGSTOREY").Single(id => Args(ifc, id, "IFCBUILDINGSTOREY")[2] == "'First'");
        var relation = Ids(ifc, "IFCRELCONTAINEDINSPATIALSTRUCTURE").Select(id => Args(ifc, id, "IFCRELCONTAINEDINSPATIALSTRUCTURE"))
            .Single(a => a[5] == $"#{first}");
        Assert.Equal($"(#{spaces["'1'"]})", relation[4]);
    }

    [Fact]
    public void RoomFinishes_BecomeTheCoveringsOfPsetSpaceCommon_WhenTheyHaveAText()
    {
        var room = Room(100, "Hall", "1");
        room.Parameters.Add(new Parameter { Id = -1006903, StorageType = StorageType.String, Value = "Screed" });   // floor finish
        room.Parameters.Add(new Parameter { Id = -1006904, StorageType = StorageType.String, Value = "" });         // wall finish, empty
        room.Parameters.Add(new Parameter { Id = -1006905, StorageType = StorageType.String, Value = "Plaster" });  // ceiling finish
        string ifc = Export(new RevitFileInfo { Elements = [Level(900), room, Room(200, "Store", "2")] });

        string[] Properties(string number)
        {
            int space = Ids(ifc, "IFCSPACE").Single(id => Args(ifc, id, "IFCSPACE")[2] == number);
            var psets = Ids(ifc, "IFCRELDEFINESBYPROPERTIES").Select(id => Args(ifc, id, "IFCRELDEFINESBYPROPERTIES"))
                .Where(a => a[4] == $"(#{space})").Select(a => int.Parse(a[5][1..])).Where(id => Ids(ifc, "IFCPROPERTYSET").Contains(id))
                .Select(id => Args(ifc, id, "IFCPROPERTYSET"));
            return Regex.Matches(psets.Single(a => a[2] == "'Pset_SpaceCommon'")[4], @"#(\d+)")
                .Select(match => Args(ifc, int.Parse(match.Groups[1].Value), "IFCPROPERTYSINGLEVALUE")).Select(a => $"{a[0]}={a[2]}").ToArray();
        }

        Assert.Equal(
            ["'Reference'=IFCIDENTIFIER('Hall 1')", "'FloorCovering'=IFCLABEL('Screed')", "'CeilingCovering'=IFCLABEL('Plaster')"],
            Properties("'1'"));
        Assert.Equal(["'Reference'=IFCIDENTIFIER('Store 2')"], Properties("'2'"));
    }

    [Fact]
    public void AreaScheme_BecomesAnIfcGroupOfItsAreas()
    {
        var scheme = new Element { Id = 15979, CategoryId = AreaSchemeCategoryId, Name = "Gross Building",
            SerializedAreaScheme = ("Gross Building", "Sum of the floor areas.") };
        scheme.SerializedExportId = Guid.Parse("1f34752d-926d-4ab8-8425-c6d81fe02dab");
        var area = Room(100, "Office", "1", categoryId: AreaCategoryId);
        area.Parameters.Add(new Parameter { Id = -1012704, StorageType = StorageType.ElementId, Value = 15979 });
        var room = Room(200, "Hall", "2");   // a room is never related to a scheme
        string ifc = Export(new RevitFileInfo { Elements = [Level(900), scheme, area, room] });

        int groupId = Ids(ifc, "IFCGROUP").Single();
        string globalId = IfcGuid.ToIfcGuid(scheme.SerializedExportId.Value);
        var group = Args(ifc, groupId, "IFCGROUP");
        Assert.Equal($"'{globalId}'", group[0]);
        Assert.Equal("'Gross Building'", group[2]);
        Assert.Equal("'Sum of the floor areas.'", group[3]);
        Assert.Equal("''", group[4]);
        var relation = Args(ifc, Ids(ifc, "IFCRELASSIGNSTOGROUP").Single(), "IFCRELASSIGNSTOGROUP");
        Assert.Equal($"'{IfcGuid.FromMd5("IfcRelAssignsToGroup:" + globalId)}'", relation[0]);
        int areaSpace = Ids(ifc, "IFCSPACE").Single(id => Args(ifc, id, "IFCSPACE")[2] == "'1'");
        Assert.Equal($"(#{areaSpace})", relation[4]);
        Assert.Equal($"#{groupId}", relation[6]);
    }

    // instance flag, type flag of the scheme, groups written
    [Theory]
    [InlineData(null, null, 1)]
    [InlineData(2, null, 0)]
    [InlineData(null, 2, 0)]
    [InlineData(1, 2, 1)]
    public void AreaScheme_WhoseExportFlagIsNo_GetsNoGroup_AndItsAreaStays(int? instanceFlag, int? typeFlag, int expected)
    {
        var scheme = new Element { Id = 15979, CategoryId = AreaSchemeCategoryId, Name = "Gross Building",
            SerializedAreaScheme = ("Gross Building", "Sum of the floor areas.") };
        var elements = new List<Element> { Level(900), scheme };
        if (instanceFlag is int own) scheme.Parameters.Add(new Parameter { Id = -1019012, StorageType = StorageType.Integer, Value = own });
        if (typeFlag is int type)
        {
            scheme.TypeId = 15980;
            elements.Add(new Element { Id = 15980, Name = "Scheme type", Parameters = [new Parameter { Id = -1019013, StorageType = StorageType.Integer, Value = type }] });
        }
        var area = Room(100, "Office", "1", categoryId: AreaCategoryId);
        area.Parameters.Add(new Parameter { Id = -1012704, StorageType = StorageType.ElementId, Value = 15979 });
        elements.Add(area);

        string ifc = Export(new RevitFileInfo { Elements = elements });

        Assert.Single(Ids(ifc, "IFCSPACE"));
        Assert.Equal(expected, Ids(ifc, "IFCGROUP").Count);
    }

    [Fact]
    public void AreaWithoutSchemeText_GetsNoGroup()
    {
        var area = Room(100, "Office", "1", categoryId: AreaCategoryId);
        area.Parameters.Add(new Parameter { Id = -1012704, StorageType = StorageType.ElementId, Value = 15979 });
        string ifc = Export(new RevitFileInfo { Elements = [Level(900), area] });
        Assert.Single(Ids(ifc, "IFCSPACE"));
        Assert.Empty(Ids(ifc, "IFCGROUP"));
    }


    private static Element Phase(int id) => new() { Id = id, CategoryId = PhaseCategoryId, Name = $"Phase {id}" };

    private static Element Level(int id, string name = "Level 1", double elevation = 0, bool buildingStory = true) => new()
    {
        Id = id, Name = name, CategoryId = -2000240, IsNativeLevel = true, SerializedFamilyId = -1, SerializedUnplacedOwnerId = -1,
        SerializedOwnerDbViewId = -1,
        Parameters =
        [
            new Parameter { Id = -1007102, Definition = new Definition { Name = "LEVEL_ELEV" }, StorageType = StorageType.Double, Value = elevation },
            new Parameter { Id = -1007111, StorageType = StorageType.Integer, Value = buildingStory ? 1 : 0 },
        ],
    };

    private static Element Room(int id, string name, string number, string? comments = null, int categoryId = RoomCategoryId,
        int levelId = 900)
    {
        var parameters = new List<Parameter>
        {
            new() { Id = -1006916, StorageType = StorageType.ElementId, Value = levelId },
            new() { Id = -1006901, StorageType = StorageType.String, Value = number },
        };
        if (comments != null) parameters.Add(new Parameter { Id = -1010106, StorageType = StorageType.String, Value = comments });
        return new Element { Id = id, CategoryId = categoryId, Category = "OST_Rooms", Name = name, Parameters = parameters };
    }

    private static string Export(RevitFileInfo info, IfcExporter? exporter = null)
    {
        string path = Path.Combine(Path.GetTempPath(), $"revitreader-spacesCurrent-{Guid.NewGuid():N}.ifc");
        try
        {
            (exporter ?? new IfcExporter()).Export(info, path);
            return File.ReadAllText(path);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static List<int> Ids(string ifc, string type) =>
        Regex.Matches(ifc, $@"#(\d+)={type}\(").Select(match => int.Parse(match.Groups[1].Value)).ToList();

    private static List<string> Args(string ifc, int id, string type)
    {
        string raw = Assert.Single(Regex.Matches(ifc, $@"#{id}={type}\((.*)\);\r?\n")).Groups[1].Value;
        var parts = new List<string>();
        int depth = 0, start = 0;
        bool inString = false;
        for (int i = 0; i < raw.Length; i++)
        {
            char c = raw[i];
            if (c == '\'') inString = !inString;
            else if (inString) continue;
            else if (c == '(') depth++;
            else if (c == ')') depth--;
            else if (c == ',' && depth == 0) { parts.Add(raw[start..i]); start = i + 1; }
        }
        parts.Add(raw[start..]);
        return parts;
    }
}
