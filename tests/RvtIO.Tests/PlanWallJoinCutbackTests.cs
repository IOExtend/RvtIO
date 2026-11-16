using RvtIO.BuiltIns;
using RvtIO.Model;
using RvtIO.Parsing;
using RvtIO.Print;
using Xunit;

namespace RvtIO.Tests;

/// <summary>The plan stops a wall flat at the near face of the wall it joins (rvt-rs #1474, RE-488):
/// a butt join (JoinType 0) to another wall of the level, orthogonal, mutual end-to-end L corners and
/// roof-joined, mitre and ambiguous ends left out.</summary>
public class PlanWallJoinCutbackTests
{
    private static void Add(Element element, BuiltInParameter id, StorageType storage, object value) =>
        element.Parameters.Add(BipHelpers.MakeBipParam((int)id, id.ToString(), storage, value));

    private static Element Level(int id, string name, double elevationFeet)
    {
        var level = new Element { Id = id, Name = name, CategoryId = (int)BuiltInCategory.OST_Levels };
        Add(level, BuiltInParameter.LEVEL_ELEV, StorageType.Double, elevationFeet);
        return level;
    }

    private static Element Wall(int id, int levelId, double x1, double y1, double x2, double y2,
        double widthFeet = 0.656168, bool roofJoined = false,
        IReadOnlyList<TfBlockDeserializer.WallEndJoin>? joins = null)
    {
        var wall = new Element
        {
            Id = id,
            CategoryId = (int)BuiltInCategory.OST_Walls,
            Location = new LocationCurve
            {
                CurveType = "Line",
                StartPoint = [x1, y1, 0],
                EndPoint = [x2, y2, 0],
            },
        };
        Add(wall, BuiltInParameter.LEVEL_PARAM, StorageType.ElementId, levelId);
        Add(wall, BuiltInParameter.WALL_ATTR_WIDTH_PARAM, StorageType.Double, widthFeet);
        if (roofJoined) wall.SerializedWallJoinedToTop = true;
        if (joins != null) wall.SerializedWallEndJoins = joins;
        return wall;
    }

    private static double Feet(double feet) => feet * 304.8;

    [Fact]
    public void StoppingEnd_IsCutAtThePartnerNearFace()
    {
        // Wall 600 runs into the middle of the vertical wall 601 (a T stop).
        var document = RvtDocument.FromInfo(new RevitFileInfo
        {
            Elements =
            [
                Level(500, "Level 1", 0),
                Wall(600, 500, 0, 0, 10, 0, joins: [new TfBlockDeserializer.WallEndJoin(1, 601, 2, 0)]),
                Wall(601, 500, 10, -5, 10, 5),
            ],
        });

        var content = PlanPrinter.CollectLevel(document, 500);

        Assert.NotNull(content);
        var wall = Assert.Single(content!.Walls, w => w.Id == 600);
        // Partner half width 100 mm back from x = 10 ft.
        Assert.Equal(Feet(0), wall.X1, 3);
        Assert.Equal(Feet(10) - 100, wall.X2, 3);
        Assert.Equal(Feet(0), wall.Y1, 3);
        Assert.Equal(Feet(0), wall.Y2, 3);
    }

    [Fact]
    public void MutualEndJoin_KeepsTheOverlap()
    {
        // An L corner both walls end at: each names the other's end.
        var document = RvtDocument.FromInfo(new RevitFileInfo
        {
            Elements =
            [
                Level(500, "Level 1", 0),
                Wall(600, 500, 0, 0, 10, 0, joins: [new TfBlockDeserializer.WallEndJoin(1, 601, 0, 0)]),
                Wall(601, 500, 10, 0, 10, 10, joins: [new TfBlockDeserializer.WallEndJoin(0, 600, 1, 0)]),
            ],
        });

        var content = PlanPrinter.CollectLevel(document, 500);

        Assert.NotNull(content);
        var wall = Assert.Single(content!.Walls, w => w.Id == 600);
        Assert.Equal(Feet(10), wall.X2, 3);
    }

    [Fact]
    public void MitreJoin_KeepsTheStoredLine()
    {
        var document = RvtDocument.FromInfo(new RevitFileInfo
        {
            Elements =
            [
                Level(500, "Level 1", 0),
                Wall(600, 500, 0, 0, 10, 0, joins: [new TfBlockDeserializer.WallEndJoin(1, 601, 2, 1)]),
                Wall(601, 500, 10, -5, 10, 5),
            ],
        });

        var content = PlanPrinter.CollectLevel(document, 500);

        Assert.NotNull(content);
        var wall = Assert.Single(content!.Walls, w => w.Id == 600);
        Assert.Equal(Feet(10), wall.X2, 3);
    }

    [Fact]
    public void RoofJoinedWall_KeepsTheStoredLine()
    {
        var document = RvtDocument.FromInfo(new RevitFileInfo
        {
            Elements =
            [
                Level(500, "Level 1", 0),
                Wall(600, 500, 0, 0, 10, 0, roofJoined: true,
                    joins: [new TfBlockDeserializer.WallEndJoin(1, 601, 2, 0)]),
                Wall(601, 500, 10, -5, 10, 5),
            ],
        });

        var content = PlanPrinter.CollectLevel(document, 500);

        Assert.NotNull(content);
        var wall = Assert.Single(content!.Walls, w => w.Id == 600);
        Assert.Equal(Feet(10), wall.X2, 3);
    }

    [Fact]
    public void StartEnd_IsCutToo()
    {
        // The wall runs from inside the partner outwards: its start is the stopping end.
        var document = RvtDocument.FromInfo(new RevitFileInfo
        {
            Elements =
            [
                Level(500, "Level 1", 0),
                Wall(600, 500, 10, 0, 20, 0, joins: [new TfBlockDeserializer.WallEndJoin(0, 601, 2, 0)]),
                Wall(601, 500, 10, -5, 10, 5),
            ],
        });

        var content = PlanPrinter.CollectLevel(document, 500);

        Assert.NotNull(content);
        var wall = Assert.Single(content!.Walls, w => w.Id == 600);
        Assert.Equal(Feet(10) + 100, wall.X1, 3);
        Assert.Equal(Feet(20), wall.X2, 3);
    }
}
