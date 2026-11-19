using System.Globalization;
using System.Text.RegularExpressions;
using RvtIO.Ifc;
using RvtIO.Model;
using RvtIO.Scene;
using Xunit;

namespace RvtIO.Tests;

/// <summary>
/// The current profile product selection (NonSpatialCollector, the default category table) and containment
/// as far as they decide which
/// products exist, which storey holds them and which pipe an insulation covers. The legacy profile keeps its
/// earlier behaviour in every case below.
/// </summary>
public class IfcExporterCurrentContainmentTests
{
    private const int GenericModel = -2000151;
    private const int PhasesCategory = -2000112; // OST_Phases: no entry in the category table
    private const int RebarCategory = -2009000;
    private const int PipeCurves = -2008044;
    private const int PipeInsulations = -2008122;
    private const int PipeAccessory = -2008055;


    [Theory]
    [InlineData(IfcProfile.Current, 628298, false)]
    [InlineData(IfcProfile.Current, -1, true)]
    [InlineData(IfcProfile.Legacy, 628298, true)]
    public void ElementOwnedByAFamily_IsAProductOnlyWithoutOwner(IfcProfile version, int familyId, bool exported)
    {
        var element = Product(100, GenericModel, "OST_GenericModel", 200);
        element.SerializedFamilyId = familyId;
        string ifc = Export(version, element);
        Assert.Equal(exported, ifc.Contains("=IFCBUILDINGELEMENTPROXY("));
    }

    [Theory]
    [InlineData(IfcProfile.Current, 700, false)]
    [InlineData(IfcProfile.Current, -1, true)]
    [InlineData(IfcProfile.Legacy, 700, true)]
    public void SlabWithAnUnplacedOwner_IsAProductOnlyWithoutOwner(IfcProfile version, int owner, bool exported)
    {
        var element = Product(100, -2000032, "OST_Floors", 200);
        element.SerializedUnplacedOwnerId = owner;
        string ifc = Export(version, element);
        Assert.Equal(exported, ifc.Contains("=IFCSLAB("));
    }

    [Theory]
    [InlineData(IfcProfile.Current, 200, true)]
    [InlineData(IfcProfile.Current, -1, false)]
    [InlineData(IfcProfile.Legacy, -1, true)]
    public void UnmappedCategory_IsAProxyOnlyAsATypedInstanceInCurrent(IfcProfile version, int typeId, bool exported)
    {
        var element = Product(100, PhasesCategory, "OST_Phases", typeId);
        string ifc = Export(version, element);
        Assert.Equal(exported, ifc.Contains("=IFCBUILDINGELEMENTPROXY("));
    }

    [Fact]
    public void RebarInstance_IsAReinforcingMeshWithAnIfcTypeProductInCurrent()
    {
        var type = new Element { Id = 200, Name = "Steel pipe", FamilyName = "Pipe IFC" };
        var rebar = Product(100, RebarCategory, "OST_Rebar", 200);
        string ifc = Export(IfcProfile.Current, type, rebar);

        Assert.DoesNotContain("=IFCBUILDINGELEMENTPROXY(", ifc);
        var mesh = Assert.Single(Regex.Matches(ifc, @"#(\d+)=IFCREINFORCINGMESH\(([^\r\n]*)\);"));
        // IfcElement (8 attributes), SteelGrade and the eight mesh dimensions.
        Assert.EndsWith(",'100',$,$,$,$,$,$,$,$,$", mesh.Groups[2].Value);
        Assert.Contains("'Pset_QuantityTakeOff'", ifc);
        Assert.DoesNotContain("Pset_BuildingElementProxyCommon", ifc);

        // IFC2X3 has no IfcReinforcingMeshType: the type is a plain IfcTypeProduct (8 attributes, Tag last).
        var typeObject = Assert.Single(Regex.Matches(ifc, @"#(\d+)=IFCTYPEPRODUCT\(([^\r\n]*)\);"));
        Assert.EndsWith(",'200'", typeObject.Groups[2].Value);
        Assert.Matches(
            $@"IFCRELDEFINESBYTYPE\([^\r\n]*,\(#{mesh.Groups[1].Value}\),#{typeObject.Groups[1].Value}\);", ifc);
    }

    [Fact]
    public void RebarInstance_StaysAProxyInLegacy()
    {
        var type = new Element { Id = 200, Name = "Steel pipe", FamilyName = "Pipe IFC" };
        string ifc = Export(IfcProfile.Legacy, type, Product(100, RebarCategory, "OST_Rebar", 200));
        Assert.Contains("=IFCBUILDINGELEMENTPROXY(", ifc);
        Assert.DoesNotContain("=IFCREINFORCINGMESH(", ifc);
    }


    [Theory]
    [InlineData(1.0, "Level B")]    // two levels at 0 ft: the later element id wins
    [InlineData(9.5, "Level B")]
    [InlineData(9.8, "Level C")]    // within 10 cm (0.328 ft) below the level at 10 ft
    [InlineData(10.0, "Level C")]
    [InlineData(25.0, "Level C")]   // the top level has no upper bound
    [InlineData(-5.0, "Level B")]   // below every level: the lowest range, the later id among equals
    public void ElementWithoutLevel_GoesToTheLastLevelThatHoldsItsBottom(double bottom, string storey)
    {
        string ifc = Export(IfcProfile.Current, Levels().Append(AtBottom(Product(100, GenericModel, "OST_GenericModel", 200), bottom)).ToArray());
        Assert.Equal(storey, StoreyOf(ifc, "IFCBUILDINGELEMENTPROXY", 100));
    }

    [Fact]
    public void ElementWithoutLevel_KeepsTheEarlierRuleInLegacy()
    {
        // The legacy profile: the highest elevation at or below the bottom (no 10 cm allowance), the first level among equals.
        string ifc = Export(IfcProfile.Legacy, Levels().Append(AtBottom(Product(100, GenericModel, "OST_GenericModel", 200), 9.8)).ToArray());
        Assert.Equal("Level A", StoreyOf(ifc, "IFCBUILDINGELEMENTPROXY", 100));
    }

    [Fact]
    public void LevelParameter_BeatsTheBoundingBox()
    {
        var element = AtBottom(Product(100, GenericModel, "OST_GenericModel", 200), 9.8);
        element.Parameters.Add(LevelParameter(-1001952, "LEVEL_PARAM", 10));
        string ifc = Export(IfcProfile.Current, Levels().Append(element).ToArray());
        Assert.Equal("Level A", StoreyOf(ifc, "IFCBUILDINGELEMENTPROXY", 100));
    }

    [Fact]
    public void SceneLevelsAndElementAssignmentsMatchCurrentIfc()
    {
        var constrained = AtBottom(Product(100, GenericModel, "OST_GenericModel", 200), 9.8);
        constrained.Parameters.Add(LevelParameter(-1001952, "LEVEL_PARAM", 10));
        var byGeometry = AtBottom(Product(101, GenericModel, "OST_GenericModel", 200), 9.8);
        var info = new RevitFileInfo { Elements = [.. Levels(), constrained, byGeometry] };
        var scene = RvtSceneReader.Read(RvtDocument.FromInfo(info), new RvtSceneOptions { ModelKey = "levels.rvt" }).Document;

        using var step = new StringWriter(CultureInfo.InvariantCulture);
        new IfcExporter { Profile = IfcProfile.Current, LengthUnit = IfcUnitFamily.Metre }.Export(info, step);
        string ifc = step.ToString();
        var ifcLevels = Regex.Matches(ifc, @"=IFCBUILDINGSTOREY\('[^']*',#\d+,'([^']*)',[^\r\n]*,\.ELEMENT\.,([^)]*)\);")
            .ToDictionary(match => match.Groups[1].Value, match => double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture));

        Assert.Equal(3, ifcLevels.Count);
        Assert.Equal(ifcLevels.Count, scene.Levels.Count);
        foreach (var level in scene.Levels)
            Assert.Equal(ifcLevels[level.Name], level.Elevation!.Value, 9);
        Assert.Equal(2, scene.Elements.Count);
        foreach (var element in scene.Elements)
            Assert.Equal(StoreyOf(ifc, "IFCBUILDINGELEMENTPROXY", element.Id),
                Assert.Single(scene.Levels, level => level.Id == element.LevelId).Name);
    }

    [Theory]
    [InlineData(IfcProfile.Current, "Level B")] // FAMILY_LEVEL_PARAM is not the associated level: the box decides
    [InlineData(IfcProfile.Legacy, "Level C")]
    public void FamilyLevelParameter_IsNotTheAssociatedLevelInCurrent(IfcProfile version, string storey)
    {
        var element = AtBottom(Product(100, GenericModel, "OST_GenericModel", 200), 1.0);
        element.Parameters.Add(LevelParameter(-1001352, "FAMILY_LEVEL_PARAM", 30));
        string ifc = Export(version, Levels().Append(element).ToArray());
        Assert.Equal(storey, StoreyOf(ifc, "IFCBUILDINGELEMENTPROXY", 100));
    }


    [Theory]
    [InlineData(IfcProfile.Legacy, "IFCSPACE")]
    [InlineData(IfcProfile.Current, "IFCBUILDINGSTOREY")]
    public void CeilingBoundingOneRoom_IsContainedInTheSpaceOnlyInLegacy(IfcProfile version, string container)
    {
        // The ceiling export reads CeilingSpaceRelCache, which nothing fills in the current profile.
        var room = new Element
        {
            Id = 300,
            CategoryId = -2000160,
            Category = "OST_Rooms",
            Name = "Room",
            Parameters =
            [
                new Parameter { Id = -1006916, StorageType = StorageType.ElementId, Value = 10 },
                new Parameter { Id = -1006901, StorageType = StorageType.String, Value = "1" },
            ],
            SerializedSpaceBoundary = Prism(0),
        };
        room.SerializedSpaceBoundary.BoundingBox = [0, 0, 0, 4, 3, 2];
        var ceiling = AtBottom(Product(100, -2000038, "OST_Ceilings", 200), 2.0);
        string ifc = Export(version, Levels().Append(room).Append(ceiling).ToArray());

        var covering = Regex.Match(ifc, @"#(\d+)=IFCCOVERING\(");
        Assert.True(covering.Success);
        foreach (Match relation in Regex.Matches(ifc, @"IFCRELCONTAINEDINSPATIALSTRUCTURE\([^\r\n]*,\(([^)]*)\),#(\d+)\);"))
            if (relation.Groups[1].Value.Split(',').Contains($"#{covering.Groups[1].Value}"))
            {
                Assert.Matches($@"#{relation.Groups[2].Value}={container}\(", ifc);
                return;
            }
        Assert.Fail("The covering is not contained in any spatial structure element.");
    }


    [Fact]
    public void PipeInsulation_IsRelatedToItsPipeInCurrent()
    {
        var pipe = Product(100, PipeCurves, "OST_PipeCurves", 200);
        var insulation = Product(101, PipeInsulations, "OST_PipeInsulations", 201);
        insulation.SerializedOwningElementId = 100;
        string ifc = Export(IfcProfile.Current, pipe, insulation);

        var pipeEntity = Regex.Match(ifc, @"#(\d+)=IFCFLOWSEGMENT\('([^']{22})'");
        var coveringEntity = Regex.Match(ifc, @"#(\d+)=IFCCOVERING\(");
        Assert.True(pipeEntity.Success && coveringEntity.Success);
        var relation = Assert.Single(Regex.Matches(ifc, @"#\d+=IFCRELCOVERSBLDGELEMENTS\('([^']{22})',#\d+,[^,]*,[^,]*,#(\d+),\(([^)]*)\)\);"));
        Assert.Equal(IfcGuid.FromMd5($"IfcRelCoversBldgElements:{pipeEntity.Groups[2].Value}"), relation.Groups[1].Value);
        Assert.Equal(pipeEntity.Groups[1].Value, relation.Groups[2].Value);
        Assert.Equal($"#{coveringEntity.Groups[1].Value}", relation.Groups[3].Value);
    }

    [Fact]
    public void PipeInsulation_IsNotRelatedInLegacy()
    {
        var pipe = Product(100, PipeCurves, "OST_PipeCurves", 200);
        var insulation = Product(101, PipeInsulations, "OST_PipeInsulations", 201);
        insulation.SerializedOwningElementId = 100;
        Assert.DoesNotContain("IFCRELCOVERSBLDGELEMENTS", Export(IfcProfile.Legacy, pipe, insulation));
    }

    [Theory]
    [InlineData(PipeCurves, 999)]    // the owner is not an exported element
    [InlineData(PipeAccessory, 100)] // a valve is not covered
    public void PipeInsulation_WithoutACoverableExportedOwner_IsNotRelated(int ownerCategory, int owner)
    {
        var host = Product(100, ownerCategory, "OST_Host", 200);
        var insulation = Product(101, PipeInsulations, "OST_PipeInsulations", 201);
        insulation.SerializedOwningElementId = owner;
        Assert.DoesNotContain("IFCRELCOVERSBLDGELEMENTS", Export(IfcProfile.Current, host, insulation));
    }

    [Theory]
    [InlineData("IFCFLOWSEGMENT", PipeCurves, true)]
    [InlineData("IFCFLOWSEGMENT", -2008000, true)]
    [InlineData("IFCFLOWFITTING", -2008049, true)]
    [InlineData("IFCFLOWFITTING", -2008010, true)]
    [InlineData("IFCFLOWFITTING", -2008126, false)] // cable tray fitting
    [InlineData("IFCFLOWSEGMENT", -2008130, false)] // cable tray
    [InlineData("IFCFLOWCONTROLLER", PipeAccessory, false)]
    [InlineData("IFCFLOWSEGMENT", null, false)]
    public void CoverableHosts(string ifcType, int? category, bool expected) =>
        Assert.Equal(expected, IfcExporter.CanHaveInsulationOrLining(ifcType, category));


    private static IEnumerable<Element> Levels() =>
    [
        Level(10, "Level A", 0.0),
        Level(20, "Level B", 0.0),
        Level(30, "Level C", 10.0),
    ];

    private static Element Level(int id, string name, double elevationFeet) => new()
    {
        Id = id,
        Name = name,
        CategoryId = -2000240,
        Category = "OST_Levels",
        IsNativeLevel = true,
        SerializedFamilyId = -1,
        SerializedUnplacedOwnerId = -1,
        SerializedOwnerDbViewId = -1,
        Parameters =
        [
            new Parameter
            {
                Id = -1007102, Definition = new Definition { Name = "LEVEL_ELEV" },
                StorageType = StorageType.Double, Value = elevationFeet,
            },
            new Parameter { Id = -1007111, StorageType = StorageType.Integer, Value = 1 },
        ],
    };

    private static Parameter LevelParameter(int id, string name, int levelId) => new()
    {
        Id = id, Definition = new Definition { Id = id, Name = name },
        StorageType = StorageType.ElementId, Value = levelId,
    };

    private static Element AtBottom(Element element, double bottom)
    {
        double[] box = [0, 0, bottom, 4, 3, bottom + 2];
        element.BoundingBox = box;
        element.Geometry = Prism(bottom);
        return element;
    }

    private static Element Product(int id, int categoryId, string category, int typeId) => new()
    {
        Id = id,
        TypeId = typeId,
        IsDiscovered = true,
        CategoryId = categoryId,
        Category = category,
        Name = $"Product {id}",
        References = [3],
        Parameters =
        [
            new Parameter { Id = -1012100, StorageType = StorageType.ElementId, Value = 3 },
            new Parameter
            {
                Id = -1002050, Definition = new Definition { Id = -1002050, Name = "ELEM_TYPE_PARAM" },
                StorageType = StorageType.ElementId, Value = typeId,
            },
        ],
        BoundingBox = [0, 0, 0, 4, 3, 2],
        Geometry = Prism(0),
    };

    private static GeometryData Prism(double bottom)
    {
        double[][] points =
        [
            [0, 0, bottom], [4, 0, bottom], [4, 3, bottom], [0, 3, bottom],
            [0, 0, bottom + 2], [4, 0, bottom + 2], [4, 3, bottom + 2], [0, 3, bottom + 2],
        ];
        int[][] faces = [[0, 3, 2, 1], [4, 5, 6, 7], [0, 1, 5, 4], [1, 2, 6, 5], [2, 3, 7, 6], [3, 0, 4, 7]];
        return new GeometryData
        {
            BoundingBox = [0, 0, bottom, 4, 3, bottom + 2],
            Faces = faces.Select(indices => new GeoFace { OuterVertices = indices.Select(index => points[index]).ToList() }).ToList(),
            BrepSolids = [new GeoBrepSolid { IsClosed = true, FaceIndices = [0, 1, 2, 3, 4, 5] }],
        };
    }

    private static string Export(IfcProfile version, params Element[] elements)
    {
        var info = new RevitFileInfo();
        info.Elements.AddRange(elements);
        string path = Path.Combine(Path.GetTempPath(), $"revitreader-currentProfile-containment-{Guid.NewGuid():N}.ifc");
        try
        {
            new IfcExporter { Profile = version }.Export(info, path);
            return File.ReadAllText(path);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    /// <summary>The name of the storey that IfcRelContainedInSpatialStructure gives the product with this tag.</summary>
    private static string? StoreyOf(string ifc, string ifcType, long tag)
    {
        var product = Regex.Match(ifc, $@"#(\d+)={ifcType}\([^\r\n]*,'{tag}'[,)]");
        if (!product.Success) return null;
        foreach (Match relation in Regex.Matches(ifc, @"IFCRELCONTAINEDINSPATIALSTRUCTURE\([^\r\n]*,\(([^)]*)\),#(\d+)\);"))
            if (relation.Groups[1].Value.Split(',').Contains($"#{product.Groups[1].Value}"))
                return Regex.Match(ifc, $@"#{relation.Groups[2].Value}=IFCBUILDINGSTOREY\('[^']*',#\d+,'([^']*)'").Groups[1].Value;
        return null;
    }
}
