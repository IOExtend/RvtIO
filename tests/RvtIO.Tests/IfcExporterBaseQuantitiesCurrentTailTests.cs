using RvtIO.Geometry;
using RvtIO.Ifc;
using RvtIO.Internal;
using RvtIO.Model;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

/// <summary>The current profile BaseQuantities, the tail of the rules: the faces the expected export sees on a wall, the perimeter of a floor with holes, the
/// ceiling area, the end faces of a room extrusion, and the Coarse detail level a door or window is measured at.</summary>
public class IfcExporterBaseQuantitiesCurrentTailTests
{
    private const double SquareFeet = 0.3048 * 0.3048;

    private static GeoFace Face(uint source, params double[][] ring)
    {
        double nx = 0, ny = 0, nz = 0;
        for (int i = 0; i < ring.Length; i++)
        {
            var a = ring[i];
            var b = ring[(i + 1) % ring.Length];
            nx += (a[1] - b[1]) * (a[2] + b[2]);
            ny += (a[2] - b[2]) * (a[0] + b[0]);
            nz += (a[0] - b[0]) * (a[1] + b[1]);
        }
        double length = Math.Sqrt(nx * nx + ny * ny + nz * nz);
        return new GeoFace { OuterVertices = ring.ToList(), Origin = ring[0], Normal = [nx / length, ny / length, nz / length], SourceFace = source };
    }

    private static GeometryData Closed(params GeoFace[] faces) => new()
    {
        Faces = faces.ToList(),
        BrepSolids = [new GeoBrepSolid { IsClosed = true, FaceIndices = Enumerable.Range(0, faces.Length).ToList() }],
    };


    private static (StepWriter W, int Owner, int Placement) NewWriter()
    {
        var w = new StepWriter();
        int owner = w.Add("IFCOWNERHISTORY", "$,$,$,.NOCHANGE.,$,$,$,0");
        int origin = w.Add("IFCCARTESIANPOINT", "(0.,0.,0.)");
        int axis = w.Add("IFCAXIS2PLACEMENT3D", $"#{origin},$,$");
        int placement = w.Add("IFCLOCALPLACEMENT", $"$,#{axis}");
        return (w, owner, placement);
    }

    /// <summary>The quantities (name, value) the pass writes for one product of the given class built from <paramref name="element"/>.</summary>
    private static List<(string Name, double Value)> QuantitiesOf(string ifcType, Element element)
    {
        var (w, owner, placement) = NewWriter();
        int product = w.Add(ifcType, $"'QZGD7y6S5209$mGLi_sPlj',#{owner},'P',$,$,#{placement},$,'1'");
        new IfcExporter().ApplyCurrentBaseQuantities(
            w, new Dictionary<int, Element> { [element.Id] = element }, new Dictionary<int, int> { [product] = element.Id },
            new Dictionary<int, (int, int, string)>(), owner, null);
        return Enumerable.Range(0, w.EntityCount).Select(w.EntityAt).Where(e => e.Type.StartsWith("IFCQUANTITY", StringComparison.Ordinal))
            .Select(e =>
            {
                var parts = IfcExporter.SplitStepArguments(e.Args);
                return (parts[0].Trim('\''), double.Parse(parts[3], System.Globalization.CultureInfo.InvariantCulture));
            }).ToList();
    }


    [Fact]
    public void VerticalFaceArea_RegionsOfOneSerializedFace_AreOneFace()
    {
        // A wall 10 x 2 ft whose long side was cut by the parser into two regions of one serialized Face (6 + 4 ft long).
        var wall = Closed(
            Face(1, [0, 0, 0], [0, 1, 0], [10, 1, 0], [10, 0, 0]),
            Face(2, [0, 0, 2], [10, 0, 2], [10, 1, 2], [0, 1, 2]),
            Face(3, [0, 0, 0], [6, 0, 0], [6, 0, 2], [0, 0, 2]),
            Face(3, [6, 0, 0], [10, 0, 0], [10, 0, 2], [6, 0, 2]),
            Face(4, [0, 1, 0], [0, 1, 2], [10, 1, 2], [10, 1, 0]),
            Face(5, [0, 0, 0], [0, 0, 2], [0, 1, 2], [0, 1, 0]),
            Face(6, [10, 0, 0], [10, 1, 0], [10, 1, 2], [10, 0, 2]));

        double area = IfcExporter.LargestVerticalFaceAreaCurrent(BrepMeasures.ShellsOfCurrent(wall));

        Assert.Equal(20, area, 9);
    }

    [Fact]
    public void VerticalFaceArea_FacesOfDifferentSerializedFacesOrOfNoKnownOne_StaySeparate()
    {
        var wall = Closed(
            Face(1, [0, 0, 0], [0, 1, 0], [10, 1, 0], [10, 0, 0]),
            Face(2, [0, 0, 2], [10, 0, 2], [10, 1, 2], [0, 1, 2]),
            Face(0, [0, 0, 0], [6, 0, 0], [6, 0, 2], [0, 0, 2]),
            Face(0, [6, 0, 0], [10, 0, 0], [10, 0, 2], [6, 0, 2]),
            Face(7, [0, 1, 0], [0, 1, 2], [4, 1, 2], [4, 1, 0]),
            Face(8, [4, 1, 0], [4, 1, 2], [10, 1, 2], [10, 1, 0]));

        Assert.Equal(12, IfcExporter.LargestVerticalFaceAreaCurrent(BrepMeasures.ShellsOfCurrent(wall)), 9);
    }


    private static GeometryData PrismWithSquareHole()
    {
        // 6 x 3 ft floor, 0.5 ft thick, with a 1 x 1 ft hole through it.
        GeoFace WithHole(GeoFace face, double z)
        {
            face.InnerVertices = [[[1, 1, z], [2, 1, z], [2, 2, z], [1, 2, z]]];
            return face;
        }
        return Closed(
            WithHole(Face(1, [0, 0, 0], [0, 3, 0], [6, 3, 0], [6, 0, 0]), 0),
            WithHole(Face(2, [0, 0, 0.5], [6, 0, 0.5], [6, 3, 0.5], [0, 3, 0.5]), 0.5),
            Face(3, [0, 0, 0], [6, 0, 0], [6, 0, 0.5], [0, 0, 0.5]),
            Face(4, [6, 0, 0], [6, 3, 0], [6, 3, 0.5], [6, 0, 0.5]),
            Face(5, [6, 3, 0], [0, 3, 0], [0, 3, 0.5], [6, 3, 0.5]),
            Face(6, [0, 3, 0], [0, 0, 0], [0, 0, 0.5], [0, 3, 0.5]),
            Face(7, [1, 1, 0], [1, 1, 0.5], [2, 1, 0.5], [2, 1, 0]),
            Face(8, [2, 1, 0], [2, 1, 0.5], [2, 2, 0.5], [2, 2, 0]),
            Face(9, [2, 2, 0], [2, 2, 0.5], [1, 2, 0.5], [1, 2, 0]),
            Face(10, [1, 2, 0], [1, 2, 0.5], [1, 1, 0.5], [1, 1, 0]));
    }

    [Fact]
    public void Slab_Perimeter_IsTheOuterLoopPlusTheLoopsOfTheHoles()
    {
        var floor = new Element { Id = 1, CategoryId = -2000032, Geometry = PrismWithSquareHole() };

        var quantities = QuantitiesOf("IFCSLAB", floor);

        Assert.Equal((18 + 4) * 0.3048, quantities.Single(q => q.Name == "Perimeter").Value, 9);
    }

    [Fact]
    public void Slab_Perimeter_HostParameterIsTheOuterLoop_TheHolesAreAddedToIt()
    {
        var floor = new Element
        {
            Id = 2, CategoryId = -2000032, Geometry = PrismWithSquareHole(),
            Parameters = [new Parameter { Id = -1001953, Value = 18.0, StorageType = StorageType.Double, Definition = new Definition { Id = -1001953, Name = "P" } }],
        };

        var quantities = QuantitiesOf("IFCSLAB", floor);

        Assert.Equal((18 + 4) * 0.3048, quantities.Single(q => q.Name == "Perimeter").Value, 9);
    }


    private static Element CeilingWithArea(double? parameterArea)
    {
        var ceiling = new Element { Id = 3, CategoryId = -2000038, Geometry = PrismWithSquareHole() };
        if (parameterArea is { } area)
            ceiling.Parameters = [new Parameter { Id = -1012805, Value = area, StorageType = StorageType.Double, Definition = new Definition { Id = -1012805, Name = "P" } }];
        return ceiling;
    }

    [Theory]
    // No parameter: the upper surface of the solid (18 - 1 ft2).
    [InlineData(null, 17.0)]
    // A parameter within half a percent of the surface is the value (it is the more exact one).
    [InlineData(17.05, 17.05)]
    // A parameter far from the surface (a sketch of several loops) gives way to the surface.
    [InlineData(12.0, 17.0)]
    public void Covering_Area_IsTheSketchAreaWhenItAgreesWithTheSolidElseTheUpperSurface(double? parameter, double expected)
    {
        var ceiling = CeilingWithArea(parameter);

        var quantities = QuantitiesOf("IFCCOVERING", ceiling);

        Assert.Equal(expected * SquareFeet, quantities.Single(q => q.Name == "GrossCeilingArea").Value, 9);
    }


    private static GeometryData RotatedRoom(double[] frameX, double[] frameY)
    {
        var room = Closed(
            Face(1, [0, 0, 0], [-2, 2, 0], [0, 4, 0], [2, 2, 0]),
            Face(2, [0, 0, 3], [2, 2, 3], [0, 4, 3], [-2, 2, 3]),
            Face(3, [0, 0, 0], [2, 2, 0], [2, 2, 3], [0, 0, 3]),
            Face(4, [2, 2, 0], [0, 4, 0], [0, 4, 3], [2, 2, 3]),
            Face(5, [0, 4, 0], [-2, 2, 0], [-2, 2, 3], [0, 4, 3]),
            Face(6, [-2, 2, 0], [0, 0, 0], [0, 0, 3], [-2, 2, 3]));
        foreach (var face in room.Faces.Take(2))
        {
            face.PlaneXAxis = frameX;
            face.PlaneYAxis = frameY;
        }
        return room;
    }

    [Fact]
    public void RoomExtrusion_TurnedRoomOnAWorldAlignedPlane_IsStillAnExtrusion()
    {
        var room = RotatedRoom([1, 0, 0], [0, 1, 0]);

        Assert.Equal((4 * Math.Sqrt(8)) * 0.3048, IfcExporter.RoomExtrusionPerimeterMetresCurrent(room)!.Value, 9);
    }

    [Fact]
    public void RoomExtrusion_TurnedRoomOnATurnedPlane_IsNoExtrusion()
    {
        double s = 1 / Math.Sqrt(2);
        var room = RotatedRoom([s, s, 0], [-s, s, 0]);

        Assert.Null(IfcExporter.RoomExtrusionPerimeterMetresCurrent(room));
    }


    private static Parameter Dimension(int id, double feet) =>
        new() { Id = id, Value = feet, StorageType = StorageType.Double, Definition = new Definition { Id = id, Name = "P" + id } };

    [Fact]
    public void Window_Area_IsHalfTheSurfaceOfTheCoarseLevelNotOfTheFineSolids()
    {
        // A fine window of 52 ft2 surface (2 x 3 x 4 ft) whose coarse level has 30 ft2.
        var box = Closed(
            Face(1, [0, 0, 0], [0, 3, 0], [2, 3, 0], [2, 0, 0]), Face(2, [0, 0, 4], [2, 0, 4], [2, 3, 4], [0, 3, 4]),
            Face(3, [0, 0, 0], [2, 0, 0], [2, 0, 4], [0, 0, 4]), Face(4, [0, 3, 0], [0, 3, 4], [2, 3, 4], [2, 3, 0]),
            Face(5, [0, 0, 0], [0, 0, 4], [0, 3, 4], [0, 3, 0]), Face(6, [2, 0, 0], [2, 3, 0], [2, 3, 4], [2, 0, 4]));
        var window = new Element { Id = 5, CategoryId = -2000014, Geometry = box, CoarseSurfaceArea = 30 };

        var quantities = QuantitiesOf("IFCWINDOW", window);

        Assert.Equal(15 * SquareFeet, quantities.Single(q => q.Name == "Area").Value, 9);
    }

    [Fact]
    public void Window_Area_WithoutACoarseLevel_IsHalfTheSurfaceOfTheSolids()
    {
        var box = Closed(
            Face(1, [0, 0, 0], [0, 3, 0], [2, 3, 0], [2, 0, 0]), Face(2, [0, 0, 4], [2, 0, 4], [2, 3, 4], [0, 3, 4]),
            Face(3, [0, 0, 0], [2, 0, 0], [2, 0, 4], [0, 0, 4]), Face(4, [0, 3, 0], [0, 3, 4], [2, 3, 4], [2, 3, 0]),
            Face(5, [0, 0, 0], [0, 0, 4], [0, 3, 4], [0, 3, 0]), Face(6, [2, 0, 0], [2, 3, 0], [2, 3, 4], [2, 0, 4]));

        var quantities = QuantitiesOf("IFCWINDOW", new Element { Id = 6, CategoryId = -2000014, Geometry = box });

        Assert.Equal(26 * SquareFeet, quantities.Single(q => q.Name == "Area").Value, 9);
    }

    [Fact]
    public void Window_Area_NoSolidsAtTheCoarseLevel_IsNotWrittenEvenWithBothDimensions()
    {
        var window = new Element
        {
            Id = 7, CategoryId = -2000014, CoarseSurfaceArea = 0,
            Parameters = [Dimension(-1001300, 3), Dimension(-1001301, 2)],
        };

        var quantities = QuantitiesOf("IFCWINDOW", window);

        Assert.DoesNotContain(quantities, q => q.Name == "Area");
        Assert.Contains(quantities, q => q.Name == "Height");
    }


    private static GRepData Block(int faces, params (int Symbol, double X)[] instances) => new()
    {
        Faces = Enumerable.Range(0, faces).Select(i => new GRepFace { Vertices = [[i, 0, 0], [i + 1, 0, 0], [i, 1, 0]] }).ToList(),
        Instances = instances.Select(instance => new GRepInstance { SymbolElemId = instance.Symbol, Transform = [1, 0, 0, 0, 1, 0, 0, 0, 1, instance.X, 0, 0] }).ToList(),
    };

    [Fact]
    public void DetailLevels_SameFacesAndInstances_AreTheSameGeometry() =>
        Assert.True(GRepParserV2.IsSameDetailLevelGeometry(Block(3, (7, 1)), Block(3, (7, 1))));

    [Fact]
    public void DetailLevels_DifferentFaceCount_AreDifferentGeometry() =>
        Assert.False(GRepParserV2.IsSameDetailLevelGeometry(Block(3), Block(2)));

    [Fact]
    public void DetailLevels_DifferentPlacementOfTheSameSymbol_AreDifferentGeometry() =>
        Assert.False(GRepParserV2.IsSameDetailLevelGeometry(Block(0, (7, 1)), Block(0, (7, 2))));

    /// <summary>A unit quad in the XY plane at the given height, wound so that its area is 1.</summary>
    private static GRepFace Quad(double z) => new()
    {
        Vertices = [[0, 0, z], [1, 0, z], [1, 1, z], [0, 1, z]], Origin = [0, 0, z], XVector = [0, 0, 1],
    };

    private static (RevitFileInfo Info, ParsingState State, Element Door) DoorWith(GRepData block)
    {
        var door = new Element { Id = 10, TypeId = 11, CategoryId = -2000023 };
        var info = new RevitFileInfo();
        info.Elements.Add(door);
        var state = new ParsingState();
        state.GrepBlocks[10] = block;
        return (info, state, door);
    }

    [Fact]
    public void CoarseSurface_DoorWhoseBlockHasACoarseLevel_IsMeasuredOnThatLevel()
    {
        var fine = new GRepData { Faces = [Quad(0), Quad(1), Quad(2)] };
        fine.Coarse = new GRepData { Faces = [Quad(0), Quad(1)] };
        var (info, state, door) = DoorWith(fine);

        ElementBuilder.ApplyCoarseDoorWindowSurfaces(info, state);

        Assert.Equal(2.0, door.CoarseSurfaceArea!.Value, 9);
    }

    [Fact]
    public void CoarseSurface_SymbolWithACoarseLevel_ReplacesTheSymbolOfTheInstance()
    {
        var symbol = new GRepData { Faces = [Quad(0), Quad(1), Quad(2), Quad(3)] };
        symbol.Coarse = new GRepData { Faces = [Quad(0)] };
        var instance = new GRepData
        {
            Instances = [new GRepInstance { SymbolElemId = 500, Transform = [1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0] }],
        };
        var (info, state, door) = DoorWith(instance);
        state.GrepBlocks[500] = symbol;

        ElementBuilder.ApplyCoarseDoorWindowSurfaces(info, state);

        Assert.Equal(1.0, door.CoarseSurfaceArea!.Value, 9);
    }

    [Fact]
    public void CoarseSurface_NoCoarseLevelAnywhere_LeavesTheAreaToTheFineSolids()
    {
        var (info, state, door) = DoorWith(new GRepData { Faces = [Quad(0), Quad(1)] });

        ElementBuilder.ApplyCoarseDoorWindowSurfaces(info, state);

        Assert.Null(door.CoarseSurfaceArea);
    }

    [Fact]
    public void CoarseSurface_OtherCategories_AreNotMeasured()
    {
        var fine = new GRepData { Faces = [Quad(0), Quad(1)] };
        fine.Coarse = new GRepData { Faces = [Quad(0)] };
        var (info, state, door) = DoorWith(fine);
        door.CategoryId = -2000080;

        ElementBuilder.ApplyCoarseDoorWindowSurfaces(info, state);

        Assert.Null(door.CoarseSurfaceArea);
    }
}
