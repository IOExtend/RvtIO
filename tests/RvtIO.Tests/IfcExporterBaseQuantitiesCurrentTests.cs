using RvtIO.Geometry;
using RvtIO.Geometry.Meshing;
using RvtIO.Parsing;
using System.Globalization;
using System.Text.RegularExpressions;
using RvtIO.Ifc;
using RvtIO.Model;
using Xunit;

namespace RvtIO.Tests;

/// <summary>The current profile BaseQuantities (IfcElementQuantity "BaseQuantities"): the solid measures of the expected export's Net* calculators,
/// the prism the expected export's body exporter finds, and the quantity sets written for each product class.</summary>
public class IfcExporterBaseQuantitiesCurrentTests
{
    private const double CubicFeet = 0.3048 * 0.3048 * 0.3048;
    private const double SquareFeet = 0.3048 * 0.3048;


    private static GeoFace Face(params double[][] ring)
    {
        // The plane origin and normal that the decoded faces carry (the wall extrusion test reads them).
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
        return new GeoFace { OuterVertices = ring.ToList(), Origin = ring[0], Normal = [nx / length, ny / length, nz / length] };
    }

    /// <summary>A closed box (0..a, 0..b, 0..c) in feet, every face wound counterclockwise seen from outside.</summary>
    private static GeometryData Box(double a, double b, double c) => new()
    {
        Faces =
        [
            Face([0, 0, 0], [0, b, 0], [a, b, 0], [a, 0, 0]),
            Face([0, 0, c], [a, 0, c], [a, b, c], [0, b, c]),
            Face([0, 0, 0], [a, 0, 0], [a, 0, c], [0, 0, c]),
            Face([0, b, 0], [0, b, c], [a, b, c], [a, b, 0]),
            Face([0, 0, 0], [0, 0, c], [0, b, c], [0, b, 0]),
            Face([a, 0, 0], [a, b, 0], [a, b, c], [a, 0, c]),
        ],
        BrepSolids = [new GeoBrepSolid { IsClosed = true, FaceIndices = [0, 1, 2, 3, 4, 5] }],
    };

    private static BrepMeasures.ShellInfo SoleShell(GeometryData geometry) => Assert.Single(BrepMeasures.ShellsOfCurrent(geometry));


    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void FloorQuantities_UseTheMeasuredClosedBodyWithItsThroughOpening(bool hasCutStep, bool validMeasure)
    {
        var geometry = Box(4, 4, 1);
        var opening = Box(1, 1, 1);
        foreach (var face in opening.Faces)
        {
            foreach (var corner in face.OuterVertices!)
            {
                corner[0]++;
                corner[1]++;
            }
            face.OuterVertices!.Reverse();
        }
        geometry.Faces[0].InnerVertices = [opening.Faces[0].OuterVertices!];
        geometry.Faces[1].InnerVertices = [opening.Faces[1].OuterVertices!];
        geometry.Faces.AddRange(opening.Faces.Skip(2));
        geometry.BrepSolids![0].FaceIndices.AddRange(Enumerable.Range(6, 4));
        foreach (var face in geometry.Faces) face.GeometryNode = 1;
        geometry.NodeMeasures = [new(1, 50, validMeasure ? 15 : 16)];
        var floor = new Element
        {
            CategoryId = -2000032, NativeRootClassName = "Floor", SerializedSystemTypeId = 20, Geometry = geometry,
            CutOutSteps = [(4137, 30, hasCutStep)],
        };

        ElementBuilder.ApplyCutFloorQuantities(floor);
        ElementBuilder.ApplyCutFloorQuantities(floor);

        if (hasCutStep && validMeasure)
        {
            Assert.Equal(new[] { 15d, 15d }, floor.Parameters.Select(p => Assert.IsType<double>(p.Value)));
            Assert.Equal(new[] { -1012805, -1012806 }, floor.Parameters.Select(p => p.Id));
        }
        else Assert.Empty(floor.Parameters);
    }

    [Theory]
    [InlineData(true, 12, false, true)]
    [InlineData(false, 12, false, false)]
    [InlineData(true, 40, false, false)]
    [InlineData(true, 12, true, false)]
    public void FloorAreaWithoutASketch_RequiresAMeasuredClosedPrismWithoutCutters(
        bool closed, double measuredVolume, bool cut, bool recover)
    {
        var geometry = Box(4, 3, 1);
        geometry.BrepSolids![0].IsClosed = closed;
        foreach (var face in geometry.Faces) face.GeometryNode = 1;
        geometry.NodeMeasures = [new(1, 38, measuredVolume)];
        var floor = new Element
        {
            CategoryId = -2000032, NativeRootClassName = "Floor", SerializedSystemTypeId = 20, Geometry = geometry,
            CuttingElementIds = cut ? [30] : [],
        };

        ElementBuilder.ApplyUnsketchedFloorArea(floor);
        ElementBuilder.ApplyUnsketchedFloorArea(floor);

        if (recover)
        {
            var parameter = Assert.Single(floor.Parameters);
            Assert.Equal(-1012805, parameter.Id);
            Assert.Equal(12, Assert.IsType<double>(parameter.Value));
        }
        else Assert.Empty(floor.Parameters);
    }

    [Theory]
    [InlineData(true, 12, 1, false, true, true)]
    [InlineData(true, 12, 2, false, true, false)]
    [InlineData(true, 12, 1, true, true, false)]
    [InlineData(false, 12, 1, false, false, false)]
    [InlineData(true, 99, 1, false, false, false)]
    public void FacetedBasicWall_ExportsOnlyTheSupportedMeasuredQuantities(
        bool closed, double volume, double typeWidth, bool wide, bool writesVolume, bool writesArea)
    {
        var geometry = Box(6, 1, 2);
        geometry.BrepSolids![0].IsClosed = closed;
        foreach (var face in geometry.Faces) face.GeometryNode = 1;
        geometry.NodeMeasures = [new(1, 40, volume)];
        var wall = new Element
        {
            Id = 10, CategoryId = -2000011, NativeRootClassName = wide ? "SWall" : null, SerializedOwnElementClassId = 3524,
            SerializedSystemTypeId = 20, ValidatedWallTypeThicknessFeet = typeWidth, Geometry = geometry,
        };
        var type = new Element { Id = 20, CategoryId = -2000011, SerializedSystemTypeClassId = 512 };
        var w = new StepWriter();
        int owner = w.Add("IFCOWNERHISTORY", "$,$,$,.ADDED.,$,$,$,0");
        int wallProduct = AddProduct(w, "IFCWALL", "1ZGD7y6S5209$mGLi_sPlj", owner, placement: 0);

        Assert.Equal(writesVolume ? 1 : 0, Apply(w, owner, new() { [10] = wall, [20] = type }, new() { [wallProduct] = 10 }));
        if (writesArea) AssertQuantities(Quantities(w, "IFCQUANTITYAREA"), ("NetSideArea", 12 * SquareFeet));
        else Assert.Empty(Quantities(w, "IFCQUANTITYAREA"));
        if (writesVolume) AssertQuantities(Quantities(w, "IFCQUANTITYVOLUME"), ("NetVolume", 12 * CubicFeet));
        else Assert.Empty(Quantities(w, "IFCQUANTITYVOLUME"));
    }

    [Theory]
    [InlineData(4137, 30, 12, false, 1, true)]
    [InlineData(4137, 31, 12, false, 1, false)]
    [InlineData(977, 30, 12, false, 1, false)]
    [InlineData(4137, 30, 99, false, 1, false)]
    [InlineData(4137, 30, 12.3, true, 1, true)]
    [InlineData(4137, 30, 12.5, true, 1, false)]
    [InlineData(4137, 30, 12.3, true, 2, false)]
    public void FacetedCutWall_RequiresTheRecordedCutsAndMatchingVolume(
        ushort cutterClass, int cutterId, double volume, bool slopedCut, double typeWidth, bool writesQuantities)
    {
        var geometry = Box(6, 1, 2);
        foreach (var face in geometry.Faces)
        {
            face.GeometryNode = 1;
            if (!slopedCut) continue;
            foreach (var vertex in face.OuterVertices!)
                if (vertex[1] == 1 && vertex[2] == 2) vertex[2] = 2.1;
        }
        geometry.NodeMeasures = [new(1, 40, volume)];
        var wall = new Element
        {
            Id = 10, CategoryId = -2000011, SerializedOwnElementClassId = 3524, SerializedSystemTypeId = 20,
            ValidatedWallTypeThicknessFeet = typeWidth, Geometry = geometry,
            CuttingElementIds = [30], CutOutSteps = [(cutterClass, cutterId, true)],
        };
        var type = new Element { Id = 20, CategoryId = -2000011, SerializedSystemTypeClassId = 512 };
        var w = new StepWriter();
        int owner = w.Add("IFCOWNERHISTORY", "$,$,$,.ADDED.,$,$,$,0");
        int wallProduct = AddProduct(w, "IFCWALL", "1ZGD7y6S5209$mGLi_sPlj", owner, placement: 0);

        Assert.Equal(writesQuantities ? 1 : 0, Apply(w, owner, new() { [10] = wall, [20] = type }, new() { [wallProduct] = 10 }));
        if (writesQuantities)
        {
            AssertQuantities(Quantities(w, "IFCQUANTITYAREA"), ("NetSideArea", (slopedCut ? 12.6 : 12) * SquareFeet));
            AssertQuantities(Quantities(w, "IFCQUANTITYVOLUME"), ("NetVolume", volume * CubicFeet));
        }
    }

    [Fact]
    public void Shells_Box_HaveTheVolumeAndTheSurfaceOfTheBox()
    {
        var shell = SoleShell(Box(2, 3, 4));

        Assert.True(shell.Closed);
        Assert.Equal(24, shell.Volume, 9);
        Assert.Equal(52, shell.Area, 9);
    }

    [Fact]
    public void Shells_ClosedShellWithAFaceWoundTheOtherWay_StillHasTheVolumeOfTheSolid()
    {
        var geometry = Box(2, 3, 4);
        // The top face is wound clockwise seen from outside: the signed sum over the faces alone would give 24 - 2 * 8 = 8.
        geometry.Faces[1].OuterVertices!.Reverse();

        var shell = SoleShell(geometry);

        Assert.Equal(24, shell.Volume, 9);
        Assert.Equal(52, shell.Area, 9);
    }

    [Fact]
    public void Shells_OpenShellWithAFaceWoundTheOtherWay_KeepsTheSignedSumOfItsFaces()
    {
        var geometry = Box(2, 3, 4);
        geometry.Faces[1].OuterVertices!.Reverse();
        geometry.BrepSolids = null;

        // An open shell has no outside to orient against: nothing is oriented, the faces are summed as they stand.
        Assert.Equal(8, SoleShell(geometry).Volume, 9);
    }

    [Fact]
    public void Shells_FaceWithAHole_SubtractsTheHoleFromTheFaceAndTheSurface()
    {
        var geometry = Box(4, 4, 1);
        // A square hole of 1 ft2 in the top face: the face keeps 15 ft2 of its 16.
        geometry.Faces[1].InnerVertices = [[[1, 1, 1], [2, 1, 1], [2, 2, 1], [1, 2, 1]]];

        Assert.Equal(15, BrepMeasures.NetFaceArea(geometry.Faces[1])!.Value, 9);
        Assert.Equal(16 + 15 + 16, SoleShell(geometry).Area, 9);
    }

    [Fact]
    public void Shells_WithoutBrepSolids_AreOneOpenShell()
    {
        var geometry = Box(1, 1, 1);
        geometry.BrepSolids = null;

        var shell = SoleShell(geometry);

        Assert.False(shell.Closed);
        Assert.Equal(6, shell.Faces.Count);
    }

    [Fact]
    public void Shells_NoFaces_NoShells() => Assert.Empty(BrepMeasures.ShellsOfCurrent(new GeometryData()));


    [Fact]
    public void Prism_Box_IsExtrudedAlongItsLongAxis()
    {
        var prism = BrepMeasures.FindPrismCurrent(SoleShell(Box(1, 2, 5)));

        Assert.NotNull(prism);
        Assert.Equal(5, prism!.Value.Depth, 9);
        Assert.Equal(2, prism.Value.ProfileArea, 9);
        Assert.Equal(6, prism.Value.OuterPerimeter, 9);
    }

    [Fact]
    public void Prism_VerticalOnly_IgnoresALongerHorizontalAxis()
    {
        var shell = SoleShell(Box(5, 2, 1));

        var any = BrepMeasures.FindPrismCurrent(shell);
        var vertical = BrepMeasures.FindPrismCurrent(shell, verticalOnly: true);

        Assert.Equal(5, any!.Value.Depth, 9);
        Assert.False(any.Value.Vertical);
        Assert.Equal(1, vertical!.Value.Depth, 9);
        Assert.True(vertical.Value.Vertical);
        Assert.Equal(10, vertical.Value.ProfileArea, 9);
        Assert.Equal(5, vertical.Value.ExtentX, 9);
        Assert.Equal(2, vertical.Value.ExtentY, 9);
    }

    [Fact]
    public void Prism_ATetrahedron_IsNotAPrism()
    {
        // No two faces of a tetrahedron face each other, so there is no pair of end faces.
        var geometry = new GeometryData
        {
            Faces =
            [
                Face([0, 0, 0], [0, 1, 0], [1, 0, 0]),
                Face([0, 0, 0], [1, 0, 0], [0, 0, 1]),
                Face([0, 0, 0], [0, 0, 1], [0, 1, 0]),
                Face([1, 0, 0], [0, 1, 0], [0, 0, 1]),
            ],
            BrepSolids = [new GeoBrepSolid { IsClosed = true, FaceIndices = [0, 1, 2, 3] }],
        };

        Assert.Null(BrepMeasures.FindPrismCurrent(SoleShell(geometry)));
    }

    [Fact]
    public void Prism_ATriangularWedge_IsAPrismAlongItsEdge()
    {
        var geometry = new GeometryData
        {
            Faces =
            [
                Face([0, 0, 0], [0, 2, 0], [2, 2, 0], [2, 0, 0]),
                Face([0, 0, 1], [2, 0, 0], [2, 2, 0], [0, 2, 1]),
                Face([0, 0, 0], [2, 0, 0], [0, 0, 1]),
                Face([0, 2, 0], [0, 2, 1], [2, 2, 0]),
                Face([0, 0, 0], [0, 0, 1], [0, 2, 1], [0, 2, 0]),
            ],
            BrepSolids = [new GeoBrepSolid { IsClosed = true, FaceIndices = [0, 1, 2, 3, 4] }],
        };

        var prism = BrepMeasures.FindPrismCurrent(SoleShell(geometry));

        Assert.Equal(2, prism!.Value.Depth, 9);
        Assert.Equal(1, prism.Value.ProfileArea, 9);
    }

    [Fact]
    public void Prism_ACurvedFace_IsNotAPrism()
    {
        var geometry = Box(1, 2, 3);
        geometry.Faces[2] = new GeoFace { OuterVertices = geometry.Faces[2].OuterVertices, CurvedSurface = true };

        Assert.Null(BrepMeasures.FindPrismCurrent(SoleShell(geometry)));
    }


    private static double[][] LoopOf(params double[][] points) => points;

    [Fact]
    public void CutLoop_InAPlaneAlongTheModelXAxis_HeightIsTheXExtentAndWidthTheVerticalOne()
    {
        var (height, width) = IfcExporter.CutLoopHeightWidthCurrent(LoopOf([0, 0, 0], [0.8, 0, 0], [0.8, 0, 2.1], [0, 0, 2.1]));

        Assert.Equal(0.8, height, 12);
        Assert.Equal(2.1, width, 12);
    }

    [Fact]
    public void CutLoop_InAPlaneAlongTheModelYAxis_HeightIsTheVerticalExtentAndWidthTheYOne()
    {
        var (height, width) = IfcExporter.CutLoopHeightWidthCurrent(LoopOf([0, 0, 0], [0, 0.8, 0], [0, 0.8, 2.1], [0, 0, 2.1]));

        Assert.Equal(2.1, height, 12);
        Assert.Equal(0.8, width, 12);
    }

    [Fact]
    public void CutLoop_InASkewedPlane_TakesTheHorizontalProjectionsAndIgnoresTheVerticalSide()
    {
        double d = 0.8 / Math.Sqrt(2);
        var (height, width) = IfcExporter.CutLoopHeightWidthCurrent(LoopOf([0, 0, 0], [d, d, 0], [d, d, 2.1], [0, 0, 2.1]));

        Assert.Equal(d, height, 12);
        Assert.Equal(d, width, 12);
    }

    [Fact]
    public void CutLoop_NoPoints_HasNoExtent() => Assert.Equal((0.0, 0.0), IfcExporter.CutLoopHeightWidthCurrent([]));

    [Fact]
    public void OpeningQuantities_DepthAndTheLoop_AreWrittenInThatOrder()
    {
        var quantities = IfcExporter.OpeningQuantitiesCurrent(0.202, LoopOf([0, 0, 0], [0.8, 0, 0], [0.8, 0, 2.1], [0, 0, 2.1]));

        Assert.Equal(["Depth", "Height", "Width"], quantities.Select(q => q.Name));
        Assert.Equal([0.202, 0.8, 2.1], quantities.Select(q => q.Value));
        Assert.All(quantities, q => Assert.Equal(IfcExporter.QuantityKind.Length, q.Kind));
    }

    [Fact]
    public void OpeningQuantities_ZeroDepthAndNoLoop_NothingToWrite() =>
        Assert.Empty(IfcExporter.OpeningQuantitiesCurrent(0, []));


    private static (StepWriter W, int Owner, int Placement) NewWriter()
    {
        var w = new StepWriter();
        int owner = w.Add("IFCOWNERHISTORY", "$,$,$,.NOCHANGE.,$,$,$,0");
        int origin = w.Add("IFCCARTESIANPOINT", "(0.,0.,0.)");
        int axis = w.Add("IFCAXIS2PLACEMENT3D", $"#{origin},$,$");
        int placement = w.Add("IFCLOCALPLACEMENT", $"$,#{axis}");
        return (w, owner, placement);
    }

    /// <summary>An extruded rectangle (X, Y, depth) as the Body of a product, extruded along the Z of its placement.</summary>
    private static int AddExtrusionBody(StepWriter w, double x, double y, double depth, bool alongPlacementY = false)
    {
        int origin = w.Add("IFCCARTESIANPOINT", "(0.,0.)");
        int profilePlacement = w.Add("IFCAXIS2PLACEMENT2D", $"#{origin},$");
        int profile = w.Add("IFCRECTANGLEPROFILEDEF", $".AREA.,$,#{profilePlacement},{StepWriter.Real(x)},{StepWriter.Real(y)}");
        int point3 = w.Add("IFCCARTESIANPOINT", "(0.,0.,0.)");
        // The solid's own frame: Z along the placement's Y when asked (a horizontal extrusion across a wall).
        string frame = alongPlacementY ? $"#{point3},#{w.Add("IFCDIRECTION", "(0.,1.,0.)")},#{w.Add("IFCDIRECTION", "(1.,0.,0.)")}" : $"#{point3},$,$";
        int position = w.Add("IFCAXIS2PLACEMENT3D", frame);
        int direction = w.Add("IFCDIRECTION", "(0.,0.,1.)");
        int solid = w.Add("IFCEXTRUDEDAREASOLID", $"#{profile},#{position},#{direction},{StepWriter.Real(depth)}");
        int context = w.Add("IFCGEOMETRICREPRESENTATIONCONTEXT", $"$,'Model',3,1.E-05,#{position},$");
        int representation = w.Add("IFCSHAPEREPRESENTATION", $"#{context},'Body','SweptSolid',(#{solid})");
        return w.Add("IFCPRODUCTDEFINITIONSHAPE", $"$,$,(#{representation})");
    }

    /// <summary>A body that is a vertical extrusion of a round tube profile (a hollow pile): outer radius and wall in the units of the writer.</summary>
    private static int AddHollowCircleBody(StepWriter w, double radius, double wall, double depth)
    {
        int origin = w.Add("IFCCARTESIANPOINT", "(0.,0.)");
        int profilePlacement = w.Add("IFCAXIS2PLACEMENT2D", $"#{origin},$");
        int profile = w.Add("IFCCIRCLEHOLLOWPROFILEDEF", $".AREA.,$,#{profilePlacement},{StepWriter.Real(radius)},{StepWriter.Real(wall)}");
        int point3 = w.Add("IFCCARTESIANPOINT", "(0.,0.,0.)");
        int position = w.Add("IFCAXIS2PLACEMENT3D", $"#{point3},$,$");
        int direction = w.Add("IFCDIRECTION", "(0.,0.,1.)");
        int solid = w.Add("IFCEXTRUDEDAREASOLID", $"#{profile},#{position},#{direction},{StepWriter.Real(depth)}");
        int context = w.Add("IFCGEOMETRICREPRESENTATIONCONTEXT", $"$,'Model',3,1.E-05,#{position},$");
        int representation = w.Add("IFCSHAPEREPRESENTATION", $"#{context},'Body','SweptSolid',(#{solid})");
        return w.Add("IFCPRODUCTDEFINITIONSHAPE", $"$,$,(#{representation})");
    }

    private static int AddProduct(StepWriter w, string type, string guid, int owner, int placement, int? shape = null) =>
        w.Add(type, $"'{guid}',#{owner},'P',$,$,#{placement},{(shape is { } id ? "#" + id : "$")},'1'");

    private static IReadOnlyDictionary<int, (int entityId, int placementId, string ifcType)> NoProducts =>
        new Dictionary<int, (int, int, string)>();

    private static int Apply(StepWriter w, int owner, Dictionary<int, Element>? elements = null, Dictionary<int, int>? elementByEntity = null,
        Dictionary<int, double>? storeyHeights = null, IfcExporter? exporter = null) =>
        (exporter ?? new IfcExporter()).ApplyCurrentBaseQuantities(w, elements ?? new(), elementByEntity ?? new(), NoProducts, owner, storeyHeights);

    private static List<string> Args(StepWriter w, string type) =>
        Enumerable.Range(0, w.EntityCount).Select(w.EntityAt).Where(e => e.Type == type).Select(e => e.Args).ToList();

    /// <summary>(name, value) of every quantity entity of one class, in the order written.</summary>
    private static List<(string Name, double Value)> Quantities(StepWriter w, string type) =>
        Args(w, type).Select(args =>
        {
            var parts = IfcExporter.SplitStepArguments(args);
            return (parts[0].Trim('\''), double.Parse(parts[3], CultureInfo.InvariantCulture));
        }).ToList();

    private static void AssertQuantities(IReadOnlyList<(string Name, double Value)> actual, params (string Name, double Value)[] expected)
    {
        Assert.Equal(expected.Select(e => e.Name), actual.Select(a => a.Name));
        for (int i = 0; i < expected.Length; i++)
            Assert.Equal(expected[i].Value, actual[i].Value, 9);
    }

    [Fact]
    public void Pass_Opening_WritesQuantityRealsWithAllSeventeenDigits()
    {
        var (w, owner, placement) = NewWriter();
        int shape = AddExtrusionBody(w, 2.1, 0.8, 0.202);
        AddProduct(w, "IFCOPENINGELEMENT", "3ZGD7y6S5209$mGLi_sPlj", owner, placement, shape);
        Apply(w, owner);

        var texts = Args(w, "IFCQUANTITYLENGTH").Select(args => IfcExporter.SplitStepArguments(args)[3]).ToList();

        Assert.Equal(3, texts.Count);
        Assert.Contains("0.20200000000000001", texts);
        Assert.All(texts, t => Assert.Equal(StepWriter.RealExact(double.Parse(t, CultureInfo.InvariantCulture)), t));
    }

    [Fact]
    public void Pass_Opening_DefaultMillimetreOutputKeepsTheDigitsOfADouble()
    {
        var (w, owner, placement) = NewWriter();
        int shape = AddExtrusionBody(w, 2.1, 0.8, 0.202);
        AddProduct(w, "IFCOPENINGELEMENT", "3ZGD7y6S5209$mGLi_sPlj", owner, placement, shape);
        Apply(w, owner);
        w.RewriteArguments(IfcLengthUnit.Converter(IfcUnitFamily.Millimetre, exactQuantities: true));

        var texts = Args(w, "IFCQUANTITYLENGTH").Select(args => IfcExporter.SplitStepArguments(args)[3]).ToList();

        Assert.Contains("202.", texts);
        Assert.All(texts, t => Assert.Equal(StepWriter.RealExact(double.Parse(t, CultureInfo.InvariantCulture)), t));
    }

    [Theory]
    [InlineData("IFCQUANTITYLENGTH", "4.7000000000000002", "4700.")]
    [InlineData("IFCQUANTITYLENGTH", "0.20200000000000001", "202.")]
    [InlineData("IFCQUANTITYLENGTH", "4.7", "4700.")]
    [InlineData("IFCQUANTITYAREA", "3.1000000000000001", "3.1000000000000001")]
    [InlineData("IFCQUANTITYVOLUME", "0.20200000000000001", "0.20200000000000001")]
    public void MillimetreConversion_QuantityTextIsTheTextOfTheScaledDouble(string type, string metres, string expected) =>
        Assert.Equal(expected, IfcExporter.SplitStepArguments(IfcLengthUnit.ToMillimetres(type, $"'Q','',$,{metres}", exactQuantities: true))[3]);

    [Fact]
    public void FootConversion_QuantityTextIsTheTextOfTheScaledDouble()
    {
        foreach (var (type, metres) in new[]
        {
            ("IFCQUANTITYLENGTH", IfcLengthUnit.MetresPerFoot),
            ("IFCQUANTITYAREA", IfcLengthUnit.SquareMetresPerSquareFoot),
            ("IFCQUANTITYVOLUME", IfcLengthUnit.CubicMetresPerCubicFoot),
        })
            Assert.Equal("1.", IfcExporter.SplitStepArguments(
                IfcLengthUnit.ToFamily(type, $"'Q','',$,{StepWriter.RealExact(metres)}", IfcUnitFamily.Foot, exact: true))[3]);
    }

    [Fact]
    public void MillimetreConversion_LegacyQuantityKeepsTheDecimalShift() =>
        Assert.Equal("50.0000000000006", IfcExporter.SplitStepArguments(IfcLengthUnit.ToMillimetres("IFCQUANTITYLENGTH", "'Q','',$,0.0500000000000006"))[3]);

    [Fact]
    public void Pass_Opening_WritesLengthQuantitiesAndTheSetWithThePlainGuidRule()
    {
        var (w, owner, placement) = NewWriter();
        int shape = AddExtrusionBody(w, 2.1, 0.8, 0.202);
        string guid = "3ZGD7y6S5209$mGLi_sPlj";
        int opening = AddProduct(w, "IFCOPENINGELEMENT", guid, owner, placement, shape);

        int sets = Apply(w, owner);

        Assert.Equal(1, sets);
        // No host: the loop is the profile (2.1 x 0.8 in the model XY plane), measured in the model frame.
        AssertQuantities(Quantities(w, "IFCQUANTITYLENGTH"), ("Depth", 0.202), ("Height", 2.1), ("Width", 0.8));
        Assert.All(Args(w, "IFCQUANTITYLENGTH"), args => Assert.Contains(",'',$,", args));
        string set = Assert.Single(Args(w, "IFCELEMENTQUANTITY"));
        Assert.StartsWith($"'{IfcGuid.FromMd5($"IfcElementQuantity:BaseQuantities:{guid}")}',#{owner},'BaseQuantities','',$,(", set);
        string relation = Assert.Single(Args(w, "IFCRELDEFINESBYPROPERTIES"));
        Assert.Equal($"'{IfcGuid.FromMd5($"IfcRelDefinesByProperties:BaseQuantities:{guid}")}',#{owner},'','',(#{opening}),#{w.EntityCount - 1}", relation);
    }

    [Fact]
    public void Pass_OpeningThatIsNotAnExtrusion_GetsNoSet()
    {
        var (w, owner, placement) = NewWriter();
        int brep = w.Add("IFCFACETEDBREP", "#1");
        int representation = w.Add("IFCSHAPEREPRESENTATION", $"#1,'Body','Brep',(#{brep})");
        int shape = w.Add("IFCPRODUCTDEFINITIONSHAPE", $"$,$,(#{representation})");
        AddProduct(w, "IFCOPENINGELEMENT", "3ZGD7y6S5209$mGLi_sPlj", owner, placement, shape);

        Assert.Equal(0, Apply(w, owner));
        Assert.Empty(Args(w, "IFCELEMENTQUANTITY"));
    }

    [Theory]
    // The opening is placed in the frame of the wall: the X side of the profile runs along the wall (Height), the Y side across (Depth).
    [InlineData("$", 0.202, 0.8, 2.1)]
    // Turned a quarter relative to the wall, the same box reads the other way round.
    [InlineData("#REF", 0.8, 0.202, 2.1)]
    public void Pass_FilledOpeningOfAWall_ReadsDepthAcrossHeightAlongAndWidthVertical(string openingReference, double depth, double height, double width)
    {
        var (w, owner, placement) = NewWriter();
        int point = w.Add("IFCCARTESIANPOINT", "(0.,0.,0.)");
        int wallAxis = w.Add("IFCAXIS2PLACEMENT3D", $"#{point},$,$");
        int wallPlacement = w.Add("IFCLOCALPLACEMENT", $"$,#{wallAxis}");
        int wall = AddProduct(w, "IFCWALLSTANDARDCASE", "1ZGD7y6S5209$mGLi_sPlj", owner, wallPlacement);
        // A vertical extrusion: profile X = 0.8, Y = 0.202, 2.1 up, placed in the frame of the wall.
        int quarterTurn = w.Add("IFCDIRECTION", "(0.,1.,0.)");
        int openingAxis = w.Add("IFCAXIS2PLACEMENT3D", $"#{point},$,{(openingReference == "$" ? "$" : "#" + quarterTurn)}");
        int openingPlacement = w.Add("IFCLOCALPLACEMENT", $"#{wallPlacement},#{openingAxis}");
        int shape = AddExtrusionBody(w, 0.8, 0.202, 2.1);
        int opening = AddProduct(w, "IFCOPENINGELEMENT", "2ZGD7y6S5209$mGLi_sPlj", owner, openingPlacement, shape);
        int door = AddProduct(w, "IFCDOOR", "3ZGD7y6S5209$mGLi_sPlj", owner, placement);
        w.Add("IFCRELVOIDSELEMENT", $"'4ZGD7y6S5209$mGLi_sPlj',#{owner},$,$,#{wall},#{opening}");
        w.Add("IFCRELFILLSELEMENT", $"'5ZGD7y6S5209$mGLi_sPlj',#{owner},$,$,#{opening},#{door}");

        Apply(w, owner, exporter: Legacy);

        AssertQuantities(Quantities(w, "IFCQUANTITYLENGTH"), ("Depth", depth), ("Height", height), ("Width", width));
    }

    [Fact]
    public void Pass_OpeningOfAWallWithoutAFiller_IsMeasuredAlongAcrossAndVerticallyForEitherExtrusionAxis()
    {
        var (w, owner, placement) = NewWriter();
        int wall = AddProduct(w, "IFCWALLSTANDARDCASE", "1ZGD7y6S5209$mGLi_sPlj", owner, placement);
        int vertical = AddProduct(w, "IFCOPENINGELEMENT", "2ZGD7y6S5209$mGLi_sPlj", owner, placement, AddExtrusionBody(w, 0.8, 0.202, 2.1));
        int horizontal = AddProduct(w, "IFCOPENINGELEMENT", "3ZGD7y6S5209$mGLi_sPlj", owner, placement, AddExtrusionBody(w, 2.1, 0.8, 0.202, alongPlacementY: true));
        w.Add("IFCRELVOIDSELEMENT", $"'4ZGD7y6S5209$mGLi_sPlj',#{owner},$,$,#{wall},#{vertical}");
        w.Add("IFCRELVOIDSELEMENT", $"'5ZGD7y6S5209$mGLi_sPlj',#{owner},$,$,#{wall},#{horizontal}");

        Apply(w, owner);

        var lengths = Quantities(w, "IFCQUANTITYLENGTH");
        Assert.Equal(6, lengths.Count);
        AssertQuantities(lengths.Take(3).ToList(), ("Depth", 0.202), ("Height", 2.1), ("Width", 0.8));
        AssertQuantities(lengths.Skip(3).ToList(), ("Depth", 0.202), ("Height", 0.8), ("Width", 2.1));
    }

    /// <summary>A wall placed with its X axis along <paramref name="direction"/>, and an opening placed in the wall frame whose
    /// body is the vertical box 0.8 along the wall by 0.202 across by 2.1 up.</summary>
    private static (StepWriter W, int Owner, int Wall) VerticalOpeningInWall(
        string direction, string hostType = "IFCWALLSTANDARDCASE", double? wallWidth = null, bool filled = false,
        string fillerType = "IFCDOOR")
    {
        var (w, owner, placement) = NewWriter();
        int point = w.Add("IFCCARTESIANPOINT", "(0.,0.,0.)");
        int wallDirection = w.Add("IFCDIRECTION", direction);
        int wallAxis = w.Add("IFCAXIS2PLACEMENT3D", $"#{point},$,#{wallDirection}");
        int wallPlacement = w.Add("IFCLOCALPLACEMENT", $"$,#{wallAxis}");
        int? wallShape = wallWidth is { } width ? AddExtrusionBody(w, 5.0, width, 3.0) : null;
        int wall = AddProduct(w, hostType, "1ZGD7y6S5209$mGLi_sPlj", owner, wallPlacement, wallShape);
        int openingAxis = w.Add("IFCAXIS2PLACEMENT3D", $"#{point},$,$");
        int openingPlacement = w.Add("IFCLOCALPLACEMENT", $"#{wallPlacement},#{openingAxis}");
        int opening = AddProduct(w, "IFCOPENINGELEMENT", "2ZGD7y6S5209$mGLi_sPlj", owner, openingPlacement, AddExtrusionBody(w, 0.8, 0.202, 2.1));
        w.Add("IFCRELVOIDSELEMENT", $"'4ZGD7y6S5209$mGLi_sPlj',#{owner},$,$,#{wall},#{opening}");
        if (filled)
        {
            int door = AddProduct(w, fillerType, "3ZGD7y6S5209$mGLi_sPlj", owner, placement);
            w.Add("IFCRELFILLSELEMENT", $"'5ZGD7y6S5209$mGLi_sPlj',#{owner},$,$,#{opening},#{door}");
        }
        return (w, owner, wall);
    }

    /// <summary>The exporter with the legacy sides, told that the opening of the writer was written from a box whose depth earlier output
    /// made 0.2 mm larger (an opening that is not told about is one of an exact depth).</summary>
    private static IfcExporter LegacyWithFormerClearance(StepWriter w)
    {
        var exporter = Legacy;
        for (int i = 0; i < w.EntityCount; i++)
            if (w.EntityAt(i).Type == "IFCOPENINGELEMENT") exporter._openingsWithFormerDepthClearanceCurrent.Add(w.EntityAt(i).Id);
        return exporter;
    }

    [Theory]
    // The legacy sides of an opening of a depth that was exact and equal to the width of its wall: made from the cut loops of the
    // family instance and measured in the frame of the wall (height along the wall, width up).
    [InlineData(0.202, true, 0.8, 2.1)]
    // The opening solid has a depth of its own: measured in the model frame (wall along Y: height up, width along the wall).
    [InlineData(0.3, true, 2.1, 0.8)]
    // Nothing fills the opening: the model frame again.
    [InlineData(0.202, false, 2.1, 0.8)]
    public void Legacy_OpeningInAWallAlongTheModelYAxis_IsMeasuredInTheFrameOfTheWallOnlyForAnExactDepthAsWideAsTheWall(
        double wallWidth, bool filled, double height, double width)
    {
        var (w, owner, _) = VerticalOpeningInWall("(0.,1.,0.)", wallWidth: wallWidth, filled: filled);

        Apply(w, owner, exporter: Legacy);

        AssertQuantities(Quantities(w, "IFCQUANTITYLENGTH").Take(3).ToList(), ("Depth", 0.202), ("Height", height), ("Width", width));
    }

    [Theory]
    [InlineData(0.202)]
    [InlineData(0.3)]
    public void Legacy_FilledOpeningWhoseDepthEarlierOutputMadeLarger_IsMeasuredInTheModelFrame(double wallWidth)
    {
        // The depth is written without the clearance now; before it was 0.2 mm more than the wall, which is why it was not taken for
        // an opening as deep as the wall.
        var (w, owner, _) = VerticalOpeningInWall("(0.,1.,0.)", wallWidth: wallWidth, filled: true);

        Apply(w, owner, exporter: LegacyWithFormerClearance(w));

        AssertQuantities(Quantities(w, "IFCQUANTITYLENGTH").Take(3).ToList(), ("Depth", 0.202), ("Height", 2.1), ("Width", 0.8));
    }

    [Fact]
    public void Legacy_FilledOpeningWhoseEarlierDepthMatchedTheWall_IsMeasuredInTheFrameOfTheWallAndKeepsItsMeasuredDepth()
    {
        // The measured depth is 0.202 and the wall is 0.2022 wide: the depth with the former clearance equals the width, so the contour
        // is measured in the frame of the wall, but the depth stays the one measured and is not set to the width.
        var (w, owner, _) = VerticalOpeningInWall("(0.,1.,0.)", wallWidth: 0.2022, filled: true);

        Apply(w, owner, exporter: LegacyWithFormerClearance(w));

        AssertQuantities(Quantities(w, "IFCQUANTITYLENGTH").Take(3).ToList(), ("Depth", 0.202), ("Height", 0.8), ("Width", 2.1));
    }

    [Fact]
    public void Legacy_FilledOpeningOfAnExactDepthInASlightlyTurnedWall_KeepsTheContourMeasuredInTheFrameOfTheWall()
    {
        // A frame in a wall 202 mm wide, turned by 1.6 degrees: the sides of the loop in the frame of the wall, not the extents of the
        // loop along X and Y.
        const string direction = "(0.999616557444871,-0.0276900357902244,0.)";
        var (w, owner, _) = VerticalOpeningInWall(direction, hostType: "IFCWALL", wallWidth: 0.202, filled: true);

        Apply(w, owner, exporter: Legacy);

        AssertQuantities(Quantities(w, "IFCQUANTITYLENGTH").Take(3).ToList(), ("Depth", 0.202), ("Height", 0.8), ("Width", 2.1));
    }

    [Fact]
    public void Default_FilledOpeningOfAnExactDepthInASlightlyTurnedWall_IsMeasuredInTheModelFrame()
    {
        // The filler is no door or window, so the host-axes measure does not apply; the default does not take the frame of the wall.
        const string direction = "(0.999616557444871,-0.0276900357902244,0.)";
        var (w, owner, _) = VerticalOpeningInWall(direction, hostType: "IFCWALL", wallWidth: 0.202, filled: true, fillerType: "IFCFURNISHINGELEMENT");

        Apply(w, owner, exporter: HostAxes);

        AssertQuantities(Quantities(w, "IFCQUANTITYLENGTH").Take(3).ToList(),
            ("Depth", 0.202), ("Height", 0.8 * 0.999616557444871), ("Width", 0.8 * 0.0276900357902244));
    }

    [Fact]
    public void Legacy_FilledOpeningWhoseDepthEarlierOutputMadeLargerInASlightlyTurnedWall_KeepsTheContourMeasuredInTheModelFrame()
    {
        const string direction = "(0.999616557444871,-0.0276900357902244,0.)";
        var (w, owner, _) = VerticalOpeningInWall(direction, hostType: "IFCWALL", wallWidth: 0.202, filled: true);

        Apply(w, owner, exporter: LegacyWithFormerClearance(w));

        AssertQuantities(Quantities(w, "IFCQUANTITYLENGTH").Take(3).ToList(),
            ("Depth", 0.202), ("Height", 0.8 * 0.999616557444871), ("Width", 0.8 * 0.0276900357902244));
    }

    [Fact]
    public void Pass_OpeningOfAWallAlongTheModelYAxis_HeightIsTheVerticalSideAndWidthTheLengthAlongTheWall()
    {
        var (w, owner, _) = VerticalOpeningInWall("(0.,1.,0.)");

        Apply(w, owner);

        AssertQuantities(Quantities(w, "IFCQUANTITYLENGTH"), ("Depth", 0.202), ("Height", 2.1), ("Width", 0.8));
    }

    [Fact]
    public void Pass_UnfilledOpeningOfASkewedWall_HeightIsVerticalAndWidthFollowsTheWall()
    {
        var (w, owner, _) = VerticalOpeningInWall("(1.,1.,0.)");

        Apply(w, owner);

        AssertQuantities(Quantities(w, "IFCQUANTITYLENGTH"), ("Depth", 0.202), ("Height", 2.1), ("Width", 0.8));
    }

    private static IfcExporter HostAxes => new();

    private static IfcExporter Legacy => new() { OpeningQuantitiesSwapped = true };

    [Theory]
    // The same door opening, 0.8 along the wall, 0.202 across, 2.1 up, in a wall of any direction: along the wall is Width.
    [InlineData("(1.,0.,0.)", true)]
    [InlineData("(0.,1.,0.)", true)]
    [InlineData("(1.,1.,0.)", true)]
    [InlineData("(-1.,0.,0.)", true)]
    // A window opening with a depth of its own is measured the same way.
    [InlineData("(0.,1.,0.)", false)]
    public void HostAxes_FilledOpeningOfAStraightWall_WidthIsAlongTheWallHeightVerticalDepthAcross(string direction, bool depthIsWallWidth)
    {
        var (w, owner, _) = VerticalOpeningInWall(direction, wallWidth: depthIsWallWidth ? 0.202 : 0.3, filled: true);

        Apply(w, owner, exporter: HostAxes);

        AssertQuantities(Quantities(w, "IFCQUANTITYLENGTH"), ("Depth", 0.202), ("Height", 2.1), ("Width", 0.8));
    }

    [Fact]
    public void Legacy_KeepsTheModelFrameValues()
    {
        var (w, owner, _) = VerticalOpeningInWall("(0.,1.,0.)", wallWidth: 0.3, filled: true);

        Apply(w, owner, exporter: Legacy);

        AssertQuantities(Quantities(w, "IFCQUANTITYLENGTH"), ("Depth", 0.202), ("Height", 2.1), ("Width", 0.8));
    }

    [Fact]
    public void HostAxes_OpeningExtrudedAcrossTheWall_WidthIsTheSideAlongTheWall()
    {
        var (w, owner, placement) = NewWriter();
        int wall = AddProduct(w, "IFCWALLSTANDARDCASE", "1ZGD7y6S5209$mGLi_sPlj", owner, placement);
        // 2.1 along X by 0.8 up (profile), extruded 0.202 along the placement Y: across the wall.
        int opening = AddProduct(w, "IFCOPENINGELEMENT", "2ZGD7y6S5209$mGLi_sPlj", owner, placement,
            AddExtrusionBody(w, 2.1, 0.8, 0.202, alongPlacementY: true));
        int door = AddProduct(w, "IFCDOOR", "3ZGD7y6S5209$mGLi_sPlj", owner, placement);
        w.Add("IFCRELVOIDSELEMENT", $"'4ZGD7y6S5209$mGLi_sPlj',#{owner},$,$,#{wall},#{opening}");
        w.Add("IFCRELFILLSELEMENT", $"'5ZGD7y6S5209$mGLi_sPlj',#{owner},$,$,#{opening},#{door}");

        Apply(w, owner, exporter: HostAxes);

        // The default reading of this body is Height 2.1 along X, Width 0.8 up (see the test above); along the wall it is the wide side.
        AssertQuantities(Quantities(w, "IFCQUANTITYLENGTH"), ("Depth", 0.202), ("Height", 0.8), ("Width", 2.1));
    }

    [Fact]
    public void HostAxes_OpeningWithoutAFiller_IsMeasuredAlongTheWall()
    {
        var (w, owner, _) = VerticalOpeningInWall("(0.,1.,0.)");

        Apply(w, owner, exporter: HostAxes);

        AssertQuantities(Quantities(w, "IFCQUANTITYLENGTH"), ("Depth", 0.202), ("Height", 2.1), ("Width", 0.8));
    }

    [Theory]
    [InlineData("$")]
    [InlineData("#999999")]
    public void HostAxes_UnknownFillerIsNotTreatedAsAnUnfilledOpening(string filler)
    {
        var (w, owner, _) = VerticalOpeningInWall("(1.,0.,0.)");
        int opening = Enumerable.Range(0, w.EntityCount).Select(w.EntityAt).Single(e => e.Type == "IFCOPENINGELEMENT").Id;
        w.Add("IFCRELFILLSELEMENT", $"'f',#{owner},$,$,#{opening},{filler}");

        Apply(w, owner);

        AssertQuantities(Quantities(w, "IFCQUANTITYLENGTH"), ("Depth", 0.202), ("Height", 0.8), ("Width", 2.1));
    }

    [Fact]
    public void HostAxes_OpeningOfACurvedWall_IsMeasuredAsBefore()
    {
        var (w, owner, wall) = VerticalOpeningInWall("(1.,0.,0.)", filled: true);
        var element = new Element { Id = 40, Location = new LocationCurve { CurveType = "Arc", Length = 10 } };

        Apply(w, owner, new() { [40] = element }, new() { [wall] = 40 }, exporter: HostAxes);

        AssertQuantities(Quantities(w, "IFCQUANTITYLENGTH"), ("Depth", 2.1), ("Height", 0.8), ("Width", 0.202));
    }

    [Theory]
    [InlineData("IFCDOOR", 0.8, 2.1)]
    [InlineData("IFCSLAB", 2.1, 0.8)]
    public void HostAxes_OnlyTheOpeningOfADoorOrWindowIsMeasuredAlongTheWall(string fillerType, double height, double width)
    {
        var (w, owner, placement) = NewWriter();
        int wall = AddProduct(w, "IFCWALLSTANDARDCASE", "1ZGD7y6S5209$mGLi_sPlj", owner, placement);
        int opening = AddProduct(w, "IFCOPENINGELEMENT", "2ZGD7y6S5209$mGLi_sPlj", owner, placement,
            AddExtrusionBody(w, 2.1, 0.8, 0.202, alongPlacementY: true));
        int filler = AddProduct(w, fillerType, "3ZGD7y6S5209$mGLi_sPlj", owner, placement);
        w.Add("IFCRELVOIDSELEMENT", $"'4ZGD7y6S5209$mGLi_sPlj',#{owner},$,$,#{wall},#{opening}");
        w.Add("IFCRELFILLSELEMENT", $"'5ZGD7y6S5209$mGLi_sPlj',#{owner},$,$,#{opening},#{filler}");

        Apply(w, owner, exporter: HostAxes);

        AssertQuantities(Quantities(w, "IFCQUANTITYLENGTH").Take(3).ToList(), ("Depth", 0.202), ("Height", height), ("Width", width));
    }

    /// <summary>A door in a wall along <paramref name="direction"/> whose opening body is a generic box (6 by 0.202 by 3) that has
    /// nothing to do with the door; the door instance holds a Width of its own (2 ft), its type a Height (7 ft) and another Width (3 ft).</summary>
    private static (StepWriter W, int Owner, int Wall, Dictionary<int, Element> Elements, Dictionary<int, int> ElementByEntity) DoorInGenericOpening(string direction)
    {
        var (w, owner, placement) = NewWriter();
        int point = w.Add("IFCCARTESIANPOINT", "(0.,0.,0.)");
        int wallAxis = w.Add("IFCAXIS2PLACEMENT3D", $"#{point},$,#{w.Add("IFCDIRECTION", direction)}");
        int wallPlacement = w.Add("IFCLOCALPLACEMENT", $"$,#{wallAxis}");
        int wall = AddProduct(w, "IFCWALLSTANDARDCASE", "1ZGD7y6S5209$mGLi_sPlj", owner, wallPlacement);
        int openingPlacement = w.Add("IFCLOCALPLACEMENT", $"#{wallPlacement},#{w.Add("IFCAXIS2PLACEMENT3D", $"#{point},$,$")}");
        int opening = AddProduct(w, "IFCOPENINGELEMENT", "2ZGD7y6S5209$mGLi_sPlj", owner, openingPlacement, AddExtrusionBody(w, 6, 0.202, 3));
        int door = AddProduct(w, "IFCDOOR", "3ZGD7y6S5209$mGLi_sPlj", owner, placement);
        w.Add("IFCRELVOIDSELEMENT", $"'4ZGD7y6S5209$mGLi_sPlj',#{owner},$,$,#{wall},#{opening}");
        w.Add("IFCRELFILLSELEMENT", $"'5ZGD7y6S5209$mGLi_sPlj',#{owner},$,$,#{opening},#{door}");
        var instance = new Element { Id = 90, Parameters = [Dimension(-1001301, 2), TypeReference(30)] };
        var type = new Element { Id = 30, Parameters = [Dimension(-1001300, 7), Dimension(-1001301, 3)] };
        return (w, owner, wall, new() { [90] = instance, [30] = type }, new() { [door] = 90 });
    }

    [Theory]
    [InlineData("(1.,0.,0.)")]
    [InlineData("(0.,1.,0.)")]
    [InlineData("(1.,1.,0.)")]
    public void FillerDimensions_HostAxes_OpeningOfADoorIsAsWideAsTheInstanceAndAsHighAsTheTypeRatherThanTheBoxOfTheGenericOpening(string direction)
    {
        var (w, owner, _, elements, elementByEntity) = DoorInGenericOpening(direction);

        Apply(w, owner, elements, elementByEntity, exporter: HostAxes);

        AssertQuantities(Quantities(w, "IFCQUANTITYLENGTH").Take(3).ToList(), ("Depth", 0.202), ("Height", 7 * 0.3048), ("Width", 2 * 0.3048));
    }

    [Fact]
    public void FillerDimensions_HostAxes_RoughDimensionsExceedingNominal_KeepsNominalDimensions()
    {
        var (w, owner, _, elements, elementByEntity) = DoorInGenericOpening("(1.,0.,0.)");
        elements[30].Parameters.Add(Dimension(-1001304, 7.333));
        elements[90].Parameters.Add(Dimension(-1001305, 2.5));

        Apply(w, owner, elements, elementByEntity, exporter: HostAxes);

        AssertQuantities(Quantities(w, "IFCQUANTITYLENGTH").Take(3).ToList(), ("Depth", 0.202), ("Height", 7 * 0.3048), ("Width", 2 * 0.3048));
    }

    [Fact]
    public void FillerDimensions_HostAxes_PanelDimensionsOfADoorWithoutACurtainWallHost_AreRead()
    {
        var (w, owner, _, elements, elementByEntity) = DoorInGenericOpening("(1.,0.,0.)");
        elements[90].Parameters = [Dimension(-1010301, 2), TypeReference(30)];
        elements[30].Parameters = [Dimension(-1010300, 7), Dimension(-1010301, 3)];

        Apply(w, owner, elements, elementByEntity, exporter: HostAxes);

        AssertQuantities(Quantities(w, "IFCQUANTITYLENGTH").Take(3).ToList(), ("Depth", 0.202), ("Height", 7 * 0.3048), ("Width", 2 * 0.3048));
    }

    [Fact]
    public void FillerDimensions_HostAxes_RoughDimensionsSmallerThanNominal_KeepsNominalDimensions()
    {
        var (w, owner, _, elements, elementByEntity) = DoorInGenericOpening("(1.,0.,0.)");
        elements[30].Parameters.Add(Dimension(-1001304, 6.8));
        elements[90].Parameters.Add(Dimension(-1001305, 1.8));

        Apply(w, owner, elements, elementByEntity, exporter: HostAxes);

        AssertQuantities(Quantities(w, "IFCQUANTITYLENGTH").Take(3).ToList(), ("Depth", 0.202), ("Height", 7 * 0.3048), ("Width", 2 * 0.3048));
    }

    [Fact]
    public void GetDoorWindowHeightAndWidth_RoughExceedingNominal_KeepsNominal()
    {
        var type = new Element
        {
            Id = 30,
            Parameters =
            [
                Dimension(-1001300, 7.0),
                Dimension(-1001304, 7.333),
                Dimension(-1001301, 3.0),
                Dimension(-1001305, 3.25),
            ]
        };
        var elem = new Element { Id = 90 };

        double h = IfcExporter.GetDoorWindowHeight(elem, type);
        double w = IfcExporter.GetDoorWindowWidth(elem, type);

        Assert.Equal(7.0 * 0.3048 * 1000, h, 3);
        Assert.Equal(3.0 * 0.3048 * 1000, w, 3);
    }

    [Fact]
    public void FillerDimensions_Legacy_OpeningKeepsTheMeasureOfItsBody()
    {
        var (w, owner, _, elements, elementByEntity) = DoorInGenericOpening("(1.,0.,0.)");

        Apply(w, owner, elements, elementByEntity, exporter: Legacy);

        AssertQuantities(Quantities(w, "IFCQUANTITYLENGTH").Take(3).ToList(), ("Depth", 0.202), ("Height", 6), ("Width", 3));
    }

    [Fact]
    public void FillerDimensions_HostAxes_DoorWithoutDimensions_IsMeasuredAsTheExtentsOfItsSolid()
    {
        var (w, owner, _, elements, elementByEntity) = DoorInGenericOpening("(0.,1.,0.)");
        elements[90] = new Element { Id = 90, Parameters = [TypeReference(30)] };
        elements[30] = new Element { Id = 30 };

        Apply(w, owner, elements, elementByEntity, exporter: HostAxes);

        // the box is 6 along the wall, 0.202 across it and 3 up
        AssertQuantities(Quantities(w, "IFCQUANTITYLENGTH").Take(3).ToList(), ("Depth", 0.202), ("Height", 3), ("Width", 6));
    }

    [Fact]
    public void FillerDimensions_HostAxes_OnlyTheWidthKnown_HeightIsTheVerticalExtentOfTheBox()
    {
        var (w, owner, _, elements, elementByEntity) = DoorInGenericOpening("(0.,1.,0.)");
        elements[90] = new Element { Id = 90, Parameters = [Dimension(-1001301, 2), TypeReference(30)] };
        elements[30] = new Element { Id = 30 };

        Apply(w, owner, elements, elementByEntity, exporter: HostAxes);

        // the box is 6 along the wall, 0.202 across it and 3 up
        AssertQuantities(Quantities(w, "IFCQUANTITYLENGTH").Take(3).ToList(), ("Depth", 0.202), ("Height", 3), ("Width", 2 * 0.3048));
    }

    [Fact]
    public void FillerDimensions_HostAxes_OnlyTheHeightKnown_WidthIsTheExtentOfTheBoxAlongTheWall()
    {
        var (w, owner, _, elements, elementByEntity) = DoorInGenericOpening("(0.,1.,0.)");
        elements[90] = new Element { Id = 90, Parameters = [TypeReference(30)] };
        elements[30] = new Element { Id = 30, Parameters = [Dimension(-1001300, 7)] };

        Apply(w, owner, elements, elementByEntity, exporter: HostAxes);

        AssertQuantities(Quantities(w, "IFCQUANTITYLENGTH").Take(3).ToList(), ("Depth", 0.202), ("Height", 7 * 0.3048), ("Width", 6));
    }

    [Fact]
    public void FillerDimensions_HostAxes_OpeningOfACurvedWall_KeepsTheMeasuredBox()
    {
        var (w, owner, wall, elements, elementByEntity) = DoorInGenericOpening("(1.,0.,0.)");
        elements[40] = new Element { Id = 40, Location = new LocationCurve { CurveType = "Arc", Length = 10 } };
        elementByEntity[wall] = 40;

        Apply(w, owner, elements, elementByEntity, exporter: HostAxes);

        AssertQuantities(Quantities(w, "IFCQUANTITYLENGTH").Take(3).ToList(), ("Depth", 3), ("Height", 6), ("Width", 0.202));
    }

    [Fact]
    public void Pass_OpeningOfACurvedWall_IsMeasuredAsWrittenWithTheVerticalSideAsItsDepth()
    {
        var (w, owner, wall) = VerticalOpeningInWall("(1.,0.,0.)");
        var element = new Element { Id = 40, Location = new LocationCurve { CurveType = "Arc", Length = 10 } };

        Apply(w, owner, new() { [40] = element }, new() { [wall] = 40 });

        AssertQuantities(Quantities(w, "IFCQUANTITYLENGTH"), ("Depth", 2.1), ("Height", 0.8), ("Width", 0.202));
    }

    [Fact]
    public void Pass_OpeningOfGenericWallWithoutAxis_DerivesFrameFromLocationLine()
    {
        var (w, owner, placement) = NewWriter();
        int wallPlacement = placement;
        int wall = AddProduct(w, "IFCWALL", "1ZGD7y6S5209$mGLi_sPlj", owner, wallPlacement);

        double u = 1.0 / Math.Sqrt(2);
        int pt = w.Add("IFCCARTESIANPOINT", "(0.,0.,0.)");
        int dirZ = w.Add("IFCDIRECTION", "(0.,0.,1.)");
        int dirX = w.Add("IFCDIRECTION", $"({StepWriter.Real(u)},{StepWriter.Real(u)},0.)");
        int openingAxis = w.Add("IFCAXIS2PLACEMENT3D", $"#{pt},#{dirZ},#{dirX}");
        int openingPlacement = w.Add("IFCLOCALPLACEMENT", $"#{placement},#{openingAxis}");
        int opening = AddProduct(w, "IFCOPENINGELEMENT", "2ZGD7y6S5209$mGLi_sPlj", owner, openingPlacement, AddExtrusionBody(w, 0.8, 0.202, 2.1));
        int door = AddProduct(w, "IFCDOOR", "3ZGD7y6S5209$mGLi_sPlj", owner, placement);
        w.Add("IFCRELVOIDSELEMENT", $"'4ZGD7y6S5209$mGLi_sPlj',#{owner},$,$,#{wall},#{opening}");
        w.Add("IFCRELFILLSELEMENT", $"'5ZGD7y6S5209$mGLi_sPlj',#{owner},$,$,#{opening},#{door}");

        var element = new Element
        {
            Id = 41,
            Location = new LocationCurve
            {
                CurveType = "Line",
                StartPoint = [0, 0, 0],
                EndPoint = [10, 10, 0]
            },
            Parameters =
            [
                new Parameter
                {
                    Id = -1001000,
                    Value = 0.202 / 0.3048,
                    StorageType = StorageType.Double,
                    Definition = new Definition { Id = -1001000, Name = "WALL_ATTR_WIDTH_PARAM" }
                }
            ]
        };

        Apply(w, owner, new() { [41] = element }, new() { [wall] = 41 }, exporter: Legacy);

        AssertQuantities(Quantities(w, "IFCQUANTITYLENGTH"), ("Depth", 0.202), ("Height", 0.8), ("Width", 2.1));
    }

    [Fact]
    public void Pass_FilledOpeningExtrudedAcrossSkewedWall_RetainsModelFrameProjections()
    {
        var (w, owner, placement) = NewWriter();
        int wallPlacement = placement;
        int wall = AddProduct(w, "IFCWALL", "1ZGD7y6S5209$mGLi_sPlj", owner, wallPlacement);

        double u = 1.0 / Math.Sqrt(2);
        int pt = w.Add("IFCCARTESIANPOINT", "(0.,0.,0.)");
        int dirZ = w.Add("IFCDIRECTION", "(0.,0.,1.)");
        int dirX = w.Add("IFCDIRECTION", $"({StepWriter.Real(u)},{StepWriter.Real(u)},0.)");
        int openingAxis = w.Add("IFCAXIS2PLACEMENT3D", $"#{pt},#{dirZ},#{dirX}");
        int openingPlacement = w.Add("IFCLOCALPLACEMENT", $"#{placement},#{openingAxis}");
        int shape = AddExtrusionBody(w, 0.885, 2.135, 0.1, alongPlacementY: true);
        int opening = AddProduct(w, "IFCOPENINGELEMENT", "2ZGD7y6S5209$mGLi_sPlj", owner, openingPlacement, shape);
        int door = AddProduct(w, "IFCDOOR", "3ZGD7y6S5209$mGLi_sPlj", owner, placement);
        w.Add("IFCRELVOIDSELEMENT", $"'4ZGD7y6S5209$mGLi_sPlj',#{owner},$,$,#{wall},#{opening}");
        w.Add("IFCRELFILLSELEMENT", $"'5ZGD7y6S5209$mGLi_sPlj',#{owner},$,$,#{opening},#{door}");

        var element = new Element
        {
            Id = 42,
            Location = new LocationCurve
            {
                CurveType = "Line",
                StartPoint = [0, 0, 0],
                EndPoint = [10, 10, 0]
            },
            Parameters =
            [
                new Parameter
                {
                    Id = -1001000,
                    Value = 0.1 / 0.3048,
                    StorageType = StorageType.Double,
                    Definition = new Definition { Id = -1001000, Name = "WALL_ATTR_WIDTH_PARAM" }
                }
            ]
        };

        Apply(w, owner, new() { [42] = element }, new() { [wall] = 42 }, exporter: Legacy);

        double proj = 0.885 / Math.Sqrt(2);
        AssertQuantities(Quantities(w, "IFCQUANTITYLENGTH"), ("Depth", 0.1), ("Height", proj), ("Width", proj));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Pass_OpeningUsesTheWallAxisRatherThanItsPlacement(bool trimmed, bool rotatedParent)
    {
        var (w, owner, placement) = NewWriter();
        int origin = w.Add("IFCCARTESIANPOINT", "(0.,0.,0.)");
        int diagonal = w.Add("IFCDIRECTION", "(1.,1.,0.)");
        int parentDirection = w.Add("IFCDIRECTION", rotatedParent ? "(0.,1.,0.)" : "(1.,0.,0.)");
        int parentAxis = w.Add("IFCAXIS2PLACEMENT3D", $"#{origin},$,#{parentDirection}");
        int parent = w.Add("IFCLOCALPLACEMENT", $"$,#{parentAxis}");
        int curve;
        if (trimmed)
        {
            int vector = w.Add("IFCVECTOR", $"#{diagonal},2.");
            int line = w.Add("IFCLINE", $"#{origin},#{vector}");
            curve = w.Add("IFCTRIMMEDCURVE", $"#{line},(IFCPARAMETERVALUE(0.)),(IFCPARAMETERVALUE(2.)),.T.,.PARAMETER.");
        }
        else
        {
            int end = w.Add("IFCCARTESIANPOINT", "(2.,2.,0.)");
            curve = w.Add("IFCPOLYLINE", $"(#{origin},#{end})");
        }
        int rep = w.Add("IFCSHAPEREPRESENTATION", $"#1,'Axis','Curve2D',(#{curve})");
        int shape = w.Add("IFCPRODUCTDEFINITIONSHAPE", $"$,$,(#{rep})");
        int wall = AddProduct(w, "IFCWALLSTANDARDCASE", "1ZGD7y6S5209$mGLi_sPlj", owner, parent, shape);
        int openingAxis = w.Add("IFCAXIS2PLACEMENT3D", $"#{origin},$,#{diagonal}");
        int openingPlacement = w.Add("IFCLOCALPLACEMENT", $"#{parent},#{openingAxis}");
        int opening = AddProduct(w, "IFCOPENINGELEMENT", "2ZGD7y6S5209$mGLi_sPlj", owner,
            openingPlacement, AddExtrusionBody(w, 1.44, 0.32145954232208834, 2.1));
        w.Add("IFCRELVOIDSELEMENT", $"'4ZGD7y6S5209$mGLi_sPlj',#{owner},$,$,#{wall},#{opening}");

        Apply(w, owner);

        AssertQuantities(Quantities(w, "IFCQUANTITYLENGTH"), ("Depth", 0.32145954232208834),
            ("Height", 2.1), ("Width", 1.44));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Pass_UnreadableWallAxisPreservesThePlacementFrame(int invalidAxis)
    {
        var (w, owner, wall) = VerticalOpeningInWall("(0.,1.,0.)");
        int point = w.Add("IFCCARTESIANPOINT", "(0.,0.)");
        int curve = invalidAxis switch
        {
            0 => w.Add("IFCPOLYLINE", $"(#{point},#{point})"),
            1 => w.Add("IFCPOLYLINE", $"(#{point},#999999)"),
            _ => w.Add("IFCCIRCLE", "#999999,1."),
        };
        int representation = w.Add("IFCSHAPEREPRESENTATION", $"#1,'Axis','Curve2D',(#{curve})");
        int shape = w.Add("IFCPRODUCTDEFINITIONSHAPE", $"$,$,(#{representation})");
        int wallIndex = Enumerable.Range(0, w.EntityCount).Single(i => w.EntityAt(i).Id == wall);
        var arguments = IfcExporter.SplitStepArguments(w.EntityAt(wallIndex).Args);
        arguments[6] = $"#{shape}";
        w.ReplaceArgs(wallIndex, string.Join(',', arguments));

        Apply(w, owner);

        AssertQuantities(Quantities(w, "IFCQUANTITYLENGTH"), ("Depth", 0.202), ("Height", 2.1), ("Width", 0.8));
    }

    [Fact]
    public void Pass_OpeningOfASlab_IsMeasuredAsWritten()
    {
        var (w, owner, _) = VerticalOpeningInWall("(1.,0.,0.)", hostType: "IFCSLAB");

        Apply(w, owner);

        AssertQuantities(Quantities(w, "IFCQUANTITYLENGTH"), ("Depth", 2.1), ("Height", 0.8), ("Width", 0.202));
    }

    [Fact]
    public void Pass_OpeningWithAPolygonProfile_MeasuresThePolygonInTheModelFrame()
    {
        var (w, owner, placement) = NewWriter();
        int[] points = [w.Add("IFCCARTESIANPOINT", "(0.,0.)"), w.Add("IFCCARTESIANPOINT", "(3.,0.)"), w.Add("IFCCARTESIANPOINT", "(0.,2.)")];
        int polyline = w.Add("IFCPOLYLINE", $"(#{points[0]},#{points[1]},#{points[2]},#{points[0]})");
        int profile = w.Add("IFCARBITRARYCLOSEDPROFILEDEF", $".AREA.,$,#{polyline}");
        int origin = w.Add("IFCCARTESIANPOINT", "(0.,0.,0.)");
        int position = w.Add("IFCAXIS2PLACEMENT3D", $"#{origin},$,$");
        int direction = w.Add("IFCDIRECTION", "(0.,0.,1.)");
        int solid = w.Add("IFCEXTRUDEDAREASOLID", $"#{profile},#{position},#{direction},0.3");
        int representation = w.Add("IFCSHAPEREPRESENTATION", $"#1,'Body','SweptSolid',(#{solid})");
        int shape = w.Add("IFCPRODUCTDEFINITIONSHAPE", $"$,$,(#{representation})");
        AddProduct(w, "IFCOPENINGELEMENT", "3ZGD7y6S5209$mGLi_sPlj", owner, placement, shape);

        Apply(w, owner);

        AssertQuantities(Quantities(w, "IFCQUANTITYLENGTH"), ("Depth", 0.3), ("Height", 3), ("Width", 2));
    }

    [Fact]
    public void Pass_SlabThatIsNoVerticalExtrusion_TakesTheSlopedSurfaceNotTheSketchAreaAndKeepsTheVolumeOnly()
    {
        var (w, owner, placement) = NewWriter();
        int slab = AddProduct(w, "IFCSLAB", "MZGD7y6S5209$mGLi_sPlj", owner, placement);
        // A sloped slab: a triangular prism lying on its side, extruded along Y.
        var wedge = new GeometryData
        {
            Faces =
            [
                Face([0, 0, 0], [0, 2, 0], [2, 2, 0], [2, 0, 0]),
                Face([0, 0, 1], [2, 0, 0], [2, 2, 0], [0, 2, 1]),
                Face([0, 0, 0], [2, 0, 0], [0, 0, 1]),
                Face([0, 2, 0], [0, 2, 1], [2, 2, 0]),
                Face([0, 0, 0], [0, 0, 1], [0, 2, 1], [0, 2, 0]),
            ],
            BrepSolids = [new GeoBrepSolid { IsClosed = true, FaceIndices = [0, 1, 2, 3, 4] }],
        };
        // The area derived from the sketch of the floor is its plan area (2 x 2); the top surface of a sloped floor is larger.
        var element = new Element { Id = 21, Geometry = wedge, Parameters = [Param(-1012805, 4)] };

        Apply(w, owner, new() { [21] = element }, new() { [slab] = 21 });

        AssertQuantities(Quantities(w, "IFCQUANTITYAREA"), ("GrossArea", 2 * Math.Sqrt(5) * SquareFeet), ("NetArea", 2 * Math.Sqrt(5) * SquareFeet));
        AssertQuantities(Quantities(w, "IFCQUANTITYVOLUME"), ("NetVolume", 2 * CubicFeet));
        Assert.Empty(Quantities(w, "IFCQUANTITYLENGTH"));
    }

    [Theory]
    [InlineData(false, 16)] // open shell: the node's surface integrals
    [InlineData(true, 16)] // closed shell: its own volume, not the node's
    public void SlabNetVolume_UsesTheNodeMeasureOnlyForAnOpenPolygonShell(bool closed, double expected)
    {
        var (w, owner, placement) = NewWriter();
        int slab = AddProduct(w, "IFCSLAB", "MZGD7y6S5209$mGLi_sPlj", owner, placement);
        var geometry = Box(4, 4, 1);
        // Away from the origin, as models are: a gap in the shell then dominates the sum over its polygons.
        foreach (var corner in geometry.Faces.SelectMany(face => face.OuterVertices!)) corner[0] += 100;
        if (!closed) geometry.Faces.RemoveAt(5);
        geometry.BrepSolids = [new GeoBrepSolid { IsClosed = closed, FaceIndices = Enumerable.Range(0, geometry.Faces.Count).ToList() }];
        foreach (var face in geometry.Faces) face.GeometryNode = 1;
        geometry.NodeMeasures = [new(1, 48, closed ? 15 : 16)];

        Apply(w, owner, new() { [21] = new Element { Id = 21, Geometry = geometry } }, new() { [slab] = 21 });

        Assert.Equal(expected * CubicFeet, Quantities(w, "IFCQUANTITYVOLUME").Single(q => q.Name == "NetVolume").Value, 9);
    }

    [Fact]
    public void Pass_SlabWithNoHostAreaAndNoPrism_TakesTheUpwardFacesAsItsTopSurface()
    {
        var (w, owner, placement) = NewWriter();
        int slab = AddProduct(w, "IFCSLAB", "MZGD7y6S5209$mGLi_sPlj", owner, placement);
        var wedge = new GeometryData
        {
            Faces =
            [
                Face([0, 0, 0], [0, 2, 0], [2, 2, 0], [2, 0, 0]),
                Face([0, 0, 1], [2, 0, 0], [2, 2, 0], [0, 2, 1]),
                Face([0, 0, 0], [2, 0, 0], [0, 0, 1]),
                Face([0, 2, 0], [0, 2, 1], [2, 2, 0]),
                Face([0, 0, 0], [0, 0, 1], [0, 2, 1], [0, 2, 0]),
            ],
            BrepSolids = [new GeoBrepSolid { IsClosed = true, FaceIndices = [0, 1, 2, 3, 4] }],
        };

        Apply(w, owner, new() { [22] = new Element { Id = 22, Geometry = wedge } }, new() { [slab] = 22 });

        // The sloped top face is 2 wide and sqrt(5) along the slope.
        AssertQuantities(Quantities(w, "IFCQUANTITYAREA"), ("GrossArea", 2 * Math.Sqrt(5) * SquareFeet), ("NetArea", 2 * Math.Sqrt(5) * SquareFeet));
    }

    [Fact]
    public void Pass_SlabWithoutGeometry_TakesTheHostAreaParameter()
    {
        var (w, owner, placement) = NewWriter();
        int slab = AddProduct(w, "IFCSLAB", "OZGD7y6S5209$mGLi_sPlj", owner, placement);

        Apply(w, owner, new() { [25] = new Element { Id = 25, Parameters = [Param(-1012805, 17.5)] } }, new() { [slab] = 25 });

        AssertQuantities(Quantities(w, "IFCQUANTITYAREA"), ("GrossArea", 17.5 * SquareFeet), ("NetArea", 17.5 * SquareFeet));
    }

    /// <summary>A vertical prism of height 1 over a half disc of the given radius whose arc is held as <paramref name="chords"/> chords;
    /// the facet over every chord is a cylinder piece with the axis through the origin.</summary>
    private static GeometryData HalfDiscPrism(double radius, int chords)
    {
        var arc = Enumerable.Range(0, chords + 1)
            .Select(k => -Math.PI / 2 + Math.PI * k / chords).Select(a => (X: radius * Math.Cos(a), Y: radius * Math.Sin(a))).ToArray();
        var geometry = PrismOver(arc, 1);
        // the polygon closes with the diameter, so the side faces 2 .. chords + 1 stand over the chords
        foreach (var facet in geometry.Faces.Skip(2).Take(chords))
        {
            facet.CurvedSurface = true;
            facet.AnalyticCylinderOrigin = [0, 0, 0];
            facet.AnalyticCylinderAxis = [0, 0, 1];
            facet.AnalyticCylinderRadius = radius;
        }
        return geometry;
    }

    [Fact]
    public void Pass_Slab_ConvexArcOfTheOutline_CountsTheCircularSegmentsOverTheChords()
    {
        var (w, owner, placement) = NewWriter();
        int slab = AddProduct(w, "IFCSLAB", "PZGD7y6S5209$mGLi_sPlj", owner, placement);

        Apply(w, owner, new() { [26] = new Element { Id = 26, Geometry = HalfDiscPrism(3, 6) } }, new() { [slab] = 26 });

        // the half disc, not the polygon of its chords (which is 0.2 ft2 smaller)
        double halfDisc = Math.PI * 9 / 2;
        AssertQuantities(Quantities(w, "IFCQUANTITYAREA"), ("GrossArea", halfDisc * SquareFeet), ("NetArea", halfDisc * SquareFeet));
    }

    [Fact]
    public void Pass_Slab_ConvexArcOfTheOutline_GivesGrossVolumeOfTheExtrusion()
    {
        var (w, owner, placement) = NewWriter();
        int slab = AddProduct(w, "IFCSLAB", "TZGD7y6S5209$mGLi_sPlj", owner, placement, AddExtrusionBody(w, 6, 3, 1));

        Apply(w, owner, new() { [30] = new Element { Id = 30, Geometry = HalfDiscPrism(3, 6) } }, new() { [slab] = 30 });

        // the half disc, not the polygon of its chords, 1 foot thick; Perimeter is not written for an outline with arcs
        double halfDisc = Math.PI * 9 / 2;
        Assert.Equal(halfDisc * CubicFeet, Quantities(w, "IFCQUANTITYVOLUME").Single(q => q.Name == "GrossVolume").Value, 9);
        Assert.DoesNotContain(Quantities(w, "IFCQUANTITYLENGTH"), q => q.Name == "Perimeter");
    }

    [Theory]
    [InlineData("IFCSLAB", true)]
    [InlineData("IFCCOLUMN", false)]
    public void Pass_HollowCircleBody_GivesOnlyASlabTheCircumferenceAndTheDiameter(string type, bool expected)
    {
        var (w, owner, placement) = NewWriter();
        int product = AddProduct(w, type, "WZGD7y6S5209$mGLi_sPlj", owner, placement, AddHollowCircleBody(w, 0.4, 0.06, 7));

        Apply(w, owner, new() { [33] = new Element { Id = 33, Geometry = HalfDiscPrism(3, 6) } }, new() { [product] = 33 });

        var lengths = Quantities(w, "IFCQUANTITYLENGTH");
        Assert.Equal(expected, lengths.Any(q => q.Name == "Perimeter"));
        if (!expected) return;
        // the circumference of the outer circle only, and the diameter
        Assert.Equal(2 * Math.PI * 0.4, lengths.Single(q => q.Name == "Perimeter").Value, 9);
        Assert.Equal(0.8, lengths.Single(q => q.Name == "Width").Value, 9);
        // the body is read, so the slab gets GrossVolume too: the area of its top surface times the depth of the extrusion
        Assert.Equal(Math.PI * 9 / 2 * CubicFeet, Quantities(w, "IFCQUANTITYVOLUME").Single(q => q.Name == "GrossVolume").Value, 9);
    }

    [Fact]
    public void Pass_Slab_CurvedSideThatIsNoVerticalCylinder_GivesNoGrossVolume()
    {
        var (w, owner, placement) = NewWriter();
        int slab = AddProduct(w, "IFCSLAB", "UZGD7y6S5209$mGLi_sPlj", owner, placement, AddExtrusionBody(w, 6, 3, 1));
        var geometry = HalfDiscPrism(3, 6);
        geometry.Faces[2].AnalyticCylinderAxis = [1, 0, 0];

        Apply(w, owner, new() { [31] = new Element { Id = 31, Geometry = geometry } }, new() { [slab] = 31 });

        Assert.DoesNotContain(Quantities(w, "IFCQUANTITYVOLUME"), q => q.Name == "GrossVolume");
    }

    [Fact]
    public void Pass_Slab_ConvexArcOfTheOutlineWithoutAnExtrusionBody_GivesNoGrossVolume()
    {
        var (w, owner, placement) = NewWriter();
        int slab = AddProduct(w, "IFCSLAB", "VZGD7y6S5209$mGLi_sPlj", owner, placement);

        Apply(w, owner, new() { [32] = new Element { Id = 32, Geometry = HalfDiscPrism(3, 6) } }, new() { [slab] = 32 });

        Assert.DoesNotContain(Quantities(w, "IFCQUANTITYVOLUME"), q => q.Name == "GrossVolume");
    }

    [Fact]
    public void Pass_Slab_ChordsWithoutTheCylinderOfTheirFacet_StayStraight()
    {
        var (w, owner, placement) = NewWriter();
        int slab = AddProduct(w, "IFCSLAB", "QZGD7y6S5209$mGLi_sPlj", owner, placement);
        var geometry = HalfDiscPrism(3, 6);
        foreach (var facet in geometry.Faces) facet.CurvedSurface = false;

        Apply(w, owner, new() { [27] = new Element { Id = 27, Geometry = geometry } }, new() { [slab] = 27 });

        // 6 chords over 180 degrees: the polygon of the points on the circle
        double polygon = 0.5 * 9 * 6 * Math.Sin(Math.PI / 6);
        AssertQuantities(Quantities(w, "IFCQUANTITYAREA"), ("GrossArea", polygon * SquareFeet), ("NetArea", polygon * SquareFeet));
    }

    [Fact]
    public void Pass_Slab_RoundHole_TakesOffTheWholeDiscNotItsPolygon()
    {
        var (w, owner, placement) = NewWriter();
        int slab = AddProduct(w, "IFCSLAB", "RZGD7y6S5209$mGLi_sPlj", owner, placement);
        // a 10 x 10 slab with a hole of radius 2 at (5, 5) whose wall is 12 cylinder facets
        const int chords = 12;
        var geometry = PrismOver(Rectangle(10, 10), 1);
        var ring = Enumerable.Range(0, chords).Select(k => -2 * Math.PI * k / chords).Select(a => (X: 5 + 2 * Math.Cos(a), Y: 5 + 2 * Math.Sin(a))).ToArray();
        foreach (int index in new[] { 0, 1 })
            geometry.Faces[index].InnerVertices = [ring.Select(p => new[] { p.X, p.Y, index == 0 ? 0.0 : 1.0 }).ToList()];
        for (int k = 0; k < chords; k++)
        {
            var (a, b) = (ring[k], ring[(k + 1) % chords]);
            var facet = Face([a.X, a.Y, 0], [b.X, b.Y, 0], [b.X, b.Y, 1], [a.X, a.Y, 1]);
            facet.CurvedSurface = true;
            facet.AnalyticCylinderOrigin = [5, 5, 0];
            facet.AnalyticCylinderAxis = [0, 0, 1];
            facet.AnalyticCylinderRadius = 2;
            geometry.Faces.Add(facet);
            geometry.BrepSolids![0].FaceIndices.Add(geometry.Faces.Count - 1);
        }

        Apply(w, owner, new() { [28] = new Element { Id = 28, Geometry = geometry } }, new() { [slab] = 28 });

        double net = 100 - Math.PI * 4;
        AssertQuantities(Quantities(w, "IFCQUANTITYAREA"), ("GrossArea", net * SquareFeet), ("NetArea", net * SquareFeet));
    }

    [Fact]
    public void Pass_Slab_WidthIsMeasuredOnTheFirstSerializedLoopOfTheLowerEndFace()
    {
        var (w, owner, placement) = NewWriter();
        int slab = AddProduct(w, "IFCSLAB", "SZGD7y6S5209$mGLi_sPlj", owner, placement);
        // a 6 x 3 slab with a 1 x 2 hole; the Face of the bottom lists the hole first, the Face of the top the outline first
        var geometry = PrismOver(Rectangle(6, 3), 0.5);
        double[][] Hole(double z) => [[2, 0.5, z], [3, 0.5, z], [3, 2.5, z], [2, 2.5, z]];
        foreach (var (face, z) in new[] { (geometry.Faces[0], 0.0), (geometry.Faces[1], 0.5) })
            face.InnerVertices = [Hole(z).ToList()];
        geometry.Faces[0].FirstSourceLoop = Hole(0);
        geometry.Faces[1].FirstSourceLoop = Rectangle(6, 3).Select(p => new[] { p.X, p.Y, 0.5 }).ToList();

        Apply(w, owner, new() { [29] = new Element { Id = 29, Geometry = geometry } }, new() { [slab] = 29 });

        Assert.Equal(1 * 0.3048, Quantities(w, "IFCQUANTITYLENGTH").Single(q => q.Name == "Width").Value, 9);
    }

    [Fact]
    public void Pass_SlabThatIsAStairLanding_HasTheWholeSurfaceAsNetAreaAndNoGrossQuantities()
    {
        var (w, owner, placement) = NewWriter();
        int slab = AddProduct(w, "IFCSLAB", "NZGD7y6S5209$mGLi_sPlj", owner, placement);
        // A landing is a vertical prism as well, but the expected export exports it without extrusion data and without HOST_AREA_COMPUTED.
        var landing = new Element { Id = 23, CategoryId = -2000920, Geometry = PrismOver(Rectangle(6, 3), 0.5) };

        Apply(w, owner, new() { [23] = landing }, new() { [slab] = 23 });

        AssertQuantities(Quantities(w, "IFCQUANTITYAREA"), ("NetArea", 45 * SquareFeet));
        AssertQuantities(Quantities(w, "IFCQUANTITYVOLUME"), ("NetVolume", 9 * CubicFeet));
        Assert.Empty(Quantities(w, "IFCQUANTITYLENGTH"));
    }

    [Fact]
    public void Pass_Slab_PerimeterIsTheHostPerimeterParameterWhenTheFloorHoldsOne()
    {
        var (w, owner, placement) = NewWriter();
        int slab = AddProduct(w, "IFCSLAB", "LZGD7y6S5209$mGLi_sPlj", owner, placement);
        var element = new Element { Id = 20, Geometry = PrismOver(Rectangle(6, 3), 0.5), Parameters = [Param(-1001953, 100)] };

        Apply(w, owner, new() { [20] = element }, new() { [slab] = 20 });

        AssertQuantities(Quantities(w, "IFCQUANTITYLENGTH"), ("Perimeter", 100 * 0.3048), ("Width", 6 * 0.3048));
    }

    [Fact]
    public void Pass_Slab_WithoutHostPerimeterAndWithoutExtrusionData_TakesThePerimeterOfItsVerticalPrism()
    {
        var (w, owner, placement) = NewWriter();
        int slab = AddProduct(w, "IFCSLAB", "KZGD7y6S5209$mGLi_sPlj", owner, placement);
        // A 3 x 2 slab turned by 30 degrees: its end planes carry axes along the slab edges, so the two corners of their (u, v)
        // envelope do not span the body and the expected export's vertical-extrusion search fails (no Width, no GrossVolume), yet the solid is a prism.
        double cos = Math.Cos(Math.PI / 6), sin = Math.Sin(Math.PI / 6);
        var geometry = PrismOver(Rectangle(3, 2).Select(p => (p.X * cos - p.Y * sin, p.X * sin + p.Y * cos)).ToArray(), 0.5);
        foreach (var face in geometry.Faces.Take(2))
        {
            face.PlaneXAxis = [cos, sin, 0];
            face.PlaneYAxis = [-sin, cos, 0];
        }
        var element = new Element { Id = 24, Geometry = geometry };

        Apply(w, owner, new() { [24] = element }, new() { [slab] = 24 });

        AssertQuantities(Quantities(w, "IFCQUANTITYLENGTH"), ("Perimeter", 10 * 0.3048));
    }

    [Fact]
    public void Pass_Plate_UsesTheQuantitySetGuidAndNetVolumeFromTheSolid()
    {
        var (w, owner, placement) = NewWriter();
        string guid = "6ZGD7y6S5209$mGLi_sPlj";
        int plate = AddProduct(w, "IFCPLATE", guid, owner, placement);
        var element = new Element { Id = 77, CategoryId = -2000170, Geometry = Box(2, 3, 4) };

        int sets = Apply(w, owner, new() { [77] = element }, new() { [plate] = 77 });

        Assert.Equal(1, sets);
        AssertQuantities(Quantities(w, "IFCQUANTITYVOLUME"), ("NetVolume", 24 * CubicFeet));
        Assert.StartsWith($"'{IfcGuid.FromMd5($"IfcElementQuantity:QuantitySet: BaseQuantities:{guid}")}'", Assert.Single(Args(w, "IFCELEMENTQUANTITY")));
    }

    [Fact]
    public void Pass_Member_HasLengthCrossSectionVolumeAndSurface()
    {
        var (w, owner, placement) = NewWriter();
        int member = AddProduct(w, "IFCMEMBER", "7ZGD7y6S5209$mGLi_sPlj", owner, placement);
        var element = new Element { Id = 78, CategoryId = -2000171, Geometry = Box(0.2, 0.4, 5) };

        Apply(w, owner, new() { [78] = element }, new() { [member] = 78 });

        AssertQuantities(Quantities(w, "IFCQUANTITYLENGTH"), ("Length", 5 * 0.3048));
        double surface = 2 * (0.2 * 0.4 + 0.2 * 5 + 0.4 * 5) * SquareFeet;
        AssertQuantities(Quantities(w, "IFCQUANTITYAREA"), ("CrossSectionArea", surface / 2), ("NetSurfaceArea", surface));
        AssertQuantities(Quantities(w, "IFCQUANTITYVOLUME"), ("NetVolume", 0.2 * 0.4 * 5 * CubicFeet));
    }

    [Fact]
    public void Pass_Door_ReadsHeightAndWidthFromTheTypeAndIgnoresBoundingBoxEstimates()
    {
        var (w, owner, placement) = NewWriter();
        int door = AddProduct(w, "IFCDOOR", "8ZGD7y6S5209$mGLi_sPlj", owner, placement);
        var instance = new Element
        {
            Id = 90,
            BoundingBoxDimensionParamIds = [-1001300],
            Parameters =
            [
                new Parameter { Id = -1001300, Value = 7.152, StorageType = StorageType.Double, Definition = new Definition { Id = -1001300, Name = "GENERIC_HEIGHT" } },
                new Parameter { Id = 1, Value = 30, StorageType = StorageType.ElementId, Definition = new Definition { Id = 1, Name = "ELEM_TYPE_PARAM" } },
            ],
        };
        var type = new Element
        {
            Id = 30,
            Parameters =
            [
                new Parameter { Id = -1001300, Value = 6.889763779527559, StorageType = StorageType.Double, Definition = new Definition { Id = -1001300, Name = "GENERIC_HEIGHT" } },
                new Parameter { Id = -1001301, Value = 2.6246719160104988, StorageType = StorageType.Double, Definition = new Definition { Id = -1001301, Name = "GENERIC_WIDTH" } },
            ],
        };

        Apply(w, owner, new() { [90] = instance, [30] = type }, new() { [door] = 90 });

        double height = 6.889763779527559 * 0.3048, width = 2.6246719160104988 * 0.3048;
        AssertQuantities(Quantities(w, "IFCQUANTITYLENGTH"), ("Height", height), ("Width", width));
        AssertQuantities(Quantities(w, "IFCQUANTITYAREA"), ("Area", height * width));
    }

    [Fact]
    public void Pass_DoorWithoutAnyDimensionParameter_TakesHeightFromTheBoxOfTheInstanceAndWidthFromTheBoxOfItsSymbol()
    {
        var (w, owner, placement) = NewWriter();
        int door = AddProduct(w, "IFCDOOR", "FZGD7y6S5209$mGLi_sPlj", owner, placement);
        // a door of a storefront: 8 ft high, 3 ft wide along the family width axis; the box of the instance is wider (3.1458) the other way
        var instance = new Element
        {
            Id = 93, Parameters = [TypeReference(33)], BoundingBox = [0, 0, 0, 3, 3.1458, 8],   // no solids at any detail level
        };
        var symbol = new Element { Id = 33, Geometry = new GeometryData { RootBoundingBox = [-1.5, -0.1, 0, 1.5, 3, 7] } };

        Apply(w, owner, new() { [93] = instance, [33] = symbol }, new() { [door] = 93 });

        AssertQuantities(Quantities(w, "IFCQUANTITYLENGTH"), ("Height", 8 * 0.3048), ("Width", 3 * 0.3048));
        AssertQuantities(Quantities(w, "IFCQUANTITYAREA"), ("Area", 24 * SquareFeet));
    }

    [Theory]
    [InlineData(12.0, 6.0)]   // a measured Coarse area keeps priority: half of it, not Height times Width
    [InlineData(0.0, -1.0)]   // a Coarse area of zero gives no Area, as before
    public void Pass_DoorWithoutDimensionParameterAndWithACoarseArea_KeepsTheCoarseAreaRule(double coarseArea, double expectedArea)
    {
        var (w, owner, placement) = NewWriter();
        int door = AddProduct(w, "IFCDOOR", "IZGD7y6S5209$mGLi_sPlj", owner, placement);
        var instance = new Element
        {
            Id = 96, Parameters = [TypeReference(36)], BoundingBox = [0, 0, 0, 3, 3.1458, 8], Geometry = Box(3, 0.2, 7), CoarseSurfaceArea = coarseArea,
        };
        var symbol = new Element { Id = 36, Geometry = new GeometryData { RootBoundingBox = [-1.5, -0.1, 0, 1.5, 3, 7] } };

        Apply(w, owner, new() { [96] = instance, [36] = symbol }, new() { [door] = 96 });

        if (expectedArea < 0) Assert.Empty(Quantities(w, "IFCQUANTITYAREA"));
        else AssertQuantities(Quantities(w, "IFCQUANTITYAREA"), ("Area", expectedArea * SquareFeet));
    }

    [Fact]
    public void Pass_WindowWithAWidthParameterOnly_KeepsAWidthWithoutTheBoxHeight()
    {
        var (w, owner, placement) = NewWriter();
        int window = AddProduct(w, "IFCWINDOW", "HZGD7y6S5209$mGLi_sPlj", owner, placement);
        var instance = new Element { Id = 95, Parameters = [Dimension(-1001301, 4)], BoundingBox = [0, 0, 0, 4, 1, 3] };

        Apply(w, owner, new() { [95] = instance }, new() { [window] = 95 });

        AssertQuantities(Quantities(w, "IFCQUANTITYLENGTH"), ("Width", 4 * 0.3048));
    }

    [Fact]
    public void Pass_DoorWithoutAnyDimensionAndWithoutBoxes_GetsNoSet()
    {
        var (w, owner, placement) = NewWriter();
        int door = AddProduct(w, "IFCDOOR", "GZGD7y6S5209$mGLi_sPlj", owner, placement);

        Apply(w, owner, new() { [94] = new Element { Id = 94 } }, new() { [door] = 94 });

        Assert.Empty(Quantities(w, "IFCQUANTITYLENGTH"));
    }

    private static Parameter Dimension(int id, double feet) =>
        new() { Id = id, Value = feet, StorageType = StorageType.Double, Definition = new Definition { Id = id, Name = "P" + id } };

    private static Parameter TypeReference(int typeId) =>
        new() { Id = 1, Value = typeId, StorageType = StorageType.ElementId, Definition = new Definition { Id = 1, Name = "ELEM_TYPE_PARAM" } };

    [Fact]
    public void Pass_Window_ATypeDimensionOfZeroIsWrittenWhenTheInstanceOnlyHoldsAnEstimate()
    {
        var (w, owner, placement) = NewWriter();
        int window = AddProduct(w, "IFCWINDOW", "DZGD7y6S5209$mGLi_sPlj", owner, placement);
        var instance = new Element
        {
            Id = 91, BoundingBoxDimensionParamIds = [-1001300, -1001301],
            Parameters = [Dimension(-1001300, 0.164), Dimension(-1001301, 6.09), TypeReference(31)],
        };
        var type = new Element { Id = 31, Parameters = [Dimension(-1001300, 0), Dimension(-1001301, 0)] };

        Apply(w, owner, new() { [91] = instance, [31] = type }, new() { [window] = 91 });

        AssertQuantities(Quantities(w, "IFCQUANTITYLENGTH"), ("Height", 0), ("Width", 0));
    }

    [Fact]
    public void Pass_Window_WithoutAWidthParameter_TakesTheExtentOfTheSolidAlongTheWidthOfTheInstanceFrame()
    {
        var (w, owner, placement) = NewWriter();
        int window = AddProduct(w, "IFCWINDOW", "EZGD7y6S5209$mGLi_sPlj", owner, placement);
        // 2 ft wide (instance X) turned a quarter to the model: the solid runs 2 ft along the model Y.
        var instance = new Element
        {
            Id = 92, BoundingBoxDimensionParamIds = [-1001301],
            Parameters = [Dimension(-1001301, 99), Dimension(-1001300, 3), TypeReference(32)],
            Transform = new InstanceTransform { BasisX = [0, 1, 0], BasisY = [-1, 0, 0], BasisZ = [0, 0, 1] },
            Geometry = Box(0.5, 2, 3),
        };

        Apply(w, owner, new() { [92] = instance, [32] = new Element { Id = 32 } }, new() { [window] = 92 });

        AssertQuantities(Quantities(w, "IFCQUANTITYLENGTH"), ("Height", 3 * 0.3048), ("Width", 2 * 0.3048));
    }

    [Fact]
    public void Pass_ProductWithoutAQuantityRule_GetsNoSet()
    {
        var (w, owner, placement) = NewWriter();
        int furniture = AddProduct(w, "IFCFURNISHINGELEMENT", "9ZGD7y6S5209$mGLi_sPlj", owner, placement);
        var element = new Element { Id = 5, Geometry = Box(1, 1, 1) };

        Assert.Equal(0, Apply(w, owner, new() { [5] = element }, new() { [furniture] = 5 }));
        Assert.Empty(Args(w, "IFCELEMENTQUANTITY"));
    }

    [Fact]
    public void Pass_InsulationOfAPipe_GetsNoSetEvenWithAnArea()
    {
        var (w, owner, placement) = NewWriter();
        int covering = AddProduct(w, "IFCCOVERING", "CZGD7y6S5209$mGLi_sPlk", owner, placement);
        var insulation = new Element
        {
            Id = 8, CategoryId = -2008122,
            Parameters = [new Parameter { Id = -1012805, Value = 3.0, StorageType = StorageType.Double, Definition = new Definition { Id = -1012805, Name = "HOST_AREA_COMPUTED" } }],
        };

        Assert.Equal(0, Apply(w, owner, new() { [8] = insulation }, new() { [covering] = 8 }));
    }

    [Fact]
    public void Pass_Covering_TakesTheComputedHostAreaAndNothingWithoutIt()
    {
        var (w, owner, placement) = NewWriter();
        int covering = AddProduct(w, "IFCCOVERING", "AZGD7y6S5209$mGLi_sPlj", owner, placement);
        int bare = AddProduct(w, "IFCCOVERING", "BZGD7y6S5209$mGLi_sPlj", owner, placement);
        var withArea = new Element
        {
            Id = 6,
            Parameters = [new Parameter { Id = -1012805, Value = 898.9096589, StorageType = StorageType.Double, Definition = new Definition { Id = -1012805, Name = "HOST_AREA_COMPUTED" } }],
        };

        int sets = Apply(w, owner, new() { [6] = withArea, [7] = new Element { Id = 7 } }, new() { [covering] = 6, [bare] = 7 });

        Assert.Equal(1, sets);
        AssertQuantities(Quantities(w, "IFCQUANTITYAREA"), ("GrossCeilingArea", 898.9096589 * SquareFeet));
    }

    /// <summary>A vertical prism over a counterclockwise polygon (feet), every face wound counterclockwise seen from outside.</summary>
    private static GeometryData PrismOver((double X, double Y)[] polygon, double height)
    {
        var faces = new List<GeoFace>
        {
            Face(polygon.AsEnumerable().Reverse().Select(p => new[] { p.X, p.Y, 0.0 }).ToArray()),
            Face(polygon.Select(p => new[] { p.X, p.Y, height }).ToArray()),
        };
        for (int i = 0; i < polygon.Length; i++)
        {
            var (ax, ay) = polygon[i];
            var (bx, by) = polygon[(i + 1) % polygon.Length];
            faces.Add(Face([ax, ay, 0], [bx, by, 0], [bx, by, height], [ax, ay, height]));
        }
        return new GeometryData
        {
            Faces = faces,
            BrepSolids = [new GeoBrepSolid { IsClosed = true, FaceIndices = Enumerable.Range(0, faces.Count).ToList() }],
        };
    }

    private static Parameter Param(int id, double value) =>
        new() { Id = id, Value = value, StorageType = StorageType.Double, Definition = new Definition { Id = id, Name = "P" + id } };

    private static (double X, double Y)[] Rectangle(double a, double b) => [(0, 0), (a, 0), (a, b), (0, b)];

    [Fact]
    public void Pass_SimpleRectangularWall_LengthIsTheTrimmedCurveWhenItDiffersFromTheWrittenRectangle()
    {
        var (w, owner, placement) = NewWriter();
        int shape = AddExtrusionBody(w, 3.2, 0.202, 2.7);
        int wall = AddProduct(w, "IFCWALLSTANDARDCASE", "CZGD7y6S5209$mGLi_sPlj", owner, placement, shape);
        double length = 3.2 / 0.3048, thickness = 0.202 / 0.3048, height = 2.7 / 0.3048, k = 1 / 0.3048;
        // the side faces of the wall reach 0.1 m further than the rectangle that was written (3.2 m).
        var element = new Element { Id = 11, CategoryId = -2000011, Geometry = Box(length, thickness, height),
            OwnWallTrimmedAxis = [[0, 0, 0], [3.3 * k, 0, 0]] };

        Apply(w, owner, new() { [11] = element }, new() { [wall] = 11 });

        AssertQuantities(Quantities(w, "IFCQUANTITYLENGTH"), ("Height", 2.7), ("Length", 3.3), ("Width", 0.202));
    }

    [Fact]
    public void Pass_SimpleRectangularWall_TrimmedCurveWithinTheMarginKeepsTheWrittenLength()
    {
        var (w, owner, placement) = NewWriter();
        int shape = AddExtrusionBody(w, 3.2, 0.202, 2.7);
        int wall = AddProduct(w, "IFCWALLSTANDARDCASE", "CZGD7y6S5209$mGLi_sPlj", owner, placement, shape);
        double length = 3.2 / 0.3048, thickness = 0.202 / 0.3048, height = 2.7 / 0.3048, k = 1 / 0.3048;
        var element = new Element { Id = 11, CategoryId = -2000011, Geometry = Box(length, thickness, height),
            OwnWallTrimmedAxis = [[0, 0, 0], [3.200004 * k, 0, 0]] };

        Apply(w, owner, new() { [11] = element }, new() { [wall] = 11 });

        AssertQuantities(Quantities(w, "IFCQUANTITYLENGTH"), ("Height", 2.7), ("Length", 3.2), ("Width", 0.202));
    }

    [Fact]
    public void Pass_SimpleRectangularWall_TakesLengthFromTheProfileAndTheSideAreaFromTheSolid()
    {
        var (w, owner, placement) = NewWriter();
        int shape = AddExtrusionBody(w, 3.2, 0.202, 2.7);
        int wall = AddProduct(w, "IFCWALLSTANDARDCASE", "CZGD7y6S5209$mGLi_sPlj", owner, placement, shape);
        double length = 3.2 / 0.3048, thickness = 0.202 / 0.3048, height = 2.7 / 0.3048;
        var element = new Element { Id = 11, CategoryId = -2000011, Geometry = Box(length, thickness, height) };

        Apply(w, owner, new() { [11] = element }, new() { [wall] = 11 });

        double side = 3.2 * 2.7;
        AssertQuantities(Quantities(w, "IFCQUANTITYLENGTH"), ("Height", 2.7), ("Length", 3.2), ("Width", 0.202));
        AssertQuantities(Quantities(w, "IFCQUANTITYAREA"), ("GrossFootprintArea", 3.2 * 0.202), ("GrossSideArea", side), ("NetSideArea", side));
        AssertQuantities(Quantities(w, "IFCQUANTITYVOLUME"), ("GrossVolume", 0.202 * side), ("NetVolume", 3.2 * 0.202 * 2.7));
        Assert.StartsWith($"'{IfcGuid.FromMd5("IfcElementQuantity:BaseQuantities:CZGD7y6S5209$mGLi_sPlj")}'", Assert.Single(Args(w, "IFCELEMENTQUANTITY")));
    }

    [Theory]
    [InlineData(11)]
    [InlineData(1101888)]
    public void Pass_SimpleRectangularWall_FootprintIsTheWidthTimesTheWholeLocationCurve(int elementId)
    {
        var (w, owner, placement) = NewWriter();
        int shape = AddExtrusionBody(w, 3.2, 0.202, 2.7);
        int wall = AddProduct(w, "IFCWALLSTANDARDCASE", "CZGD7y6S5209$mGLi_sPlj", owner, placement, shape);
        double length = 3.2 / 0.3048, thickness = 0.202 / 0.3048, height = 2.7 / 0.3048;
        // The location curve runs 0.8 m past the face trimmed at the joins.
        var element = new Element
        {
            Id = elementId, CategoryId = -2000011, Geometry = Box(length, thickness, height),
            Location = new LocationCurve { Length = 4.0 / 0.3048 },
        };

        Apply(w, owner, new() { [elementId] = element }, new() { [wall] = elementId });

        AssertQuantities(Quantities(w, "IFCQUANTITYAREA"), ("GrossFootprintArea", 0.202 * 4.0), ("GrossSideArea", 3.2 * 2.7), ("NetSideArea", 3.2 * 2.7));
        Assert.Equal(3.2 * 0.202 * 2.7, Quantities(w, "IFCQUANTITYVOLUME").Single(q => q.Name == "NetVolume").Value, 9);
        Assert.Equal(3.2, Quantities(w, "IFCQUANTITYLENGTH").Single(q => q.Name == "Length").Value, 9);
    }

    private static Element WallTypeWithLayers(int id, params double[] layerWidthsMetres) => new()
    {
        Id = id, CategoryId = -2000011, SerializedSystemTypeClassId = 512,
        SerializedHostTypeLayers = layerWidthsMetres.Select(width => new RvtIO.Internal.RawCompoundStructureLayer(
            BitConverter.DoubleToInt64Bits(width / 0.3048), 0, 0, 0, 0, 0, 0)).ToArray(),
    };

    [Fact]
    public void Pass_SimpleRectangularWall_WidthIsTheTotalOfTheLayersOfItsType()
    {
        var (w, owner, placement) = NewWriter();
        int shape = AddExtrusionBody(w, 3.2, 0.202, 2.7);
        int wall = AddProduct(w, "IFCWALLSTANDARDCASE", "CZGD7y6S5209$mGLi_sPlj", owner, placement, shape);
        double length = 3.2 / 0.3048, thickness = 0.202 / 0.3048, height = 2.7 / 0.3048;
        // The solid is 0.202 m thick, the layers of the type add up to 0.25 m: Width, the footprint and GrossVolume follow the layers.
        var element = new Element { Id = 11, CategoryId = -2000011, SerializedSystemTypeId = 12, Geometry = Box(length, thickness, height),
            Location = new LocationCurve { Length = 3.2 / 0.3048 } };

        Apply(w, owner, new() { [11] = element, [12] = WallTypeWithLayers(12, 0.1, 0.15) }, new() { [wall] = 11 });

        double side = 3.2 * 2.7;
        AssertQuantities(Quantities(w, "IFCQUANTITYLENGTH"), ("Height", 2.7), ("Length", 3.2), ("Width", 0.25));
        AssertQuantities(Quantities(w, "IFCQUANTITYAREA"), ("GrossFootprintArea", 3.2 * 0.25), ("GrossSideArea", side), ("NetSideArea", side));
        AssertQuantities(Quantities(w, "IFCQUANTITYVOLUME"), ("GrossVolume", 0.25 * side), ("NetVolume", 3.2 * 0.202 * 2.7));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Pass_ArcWall_TheLayersOfItsTypeLeaveNetVolumeAsItIsWithoutThem(bool withTypeLayers)
    {
        var (w, owner, placement) = NewWriter();
        int shape = AddExtrusionBody(w, 3.2, 0.202, 2.7);
        int wall = AddProduct(w, "IFCWALLSTANDARDCASE", "CZGD7y6S5209$mGLi_sPlj", owner, placement, shape);
        double length = 3.2 / 0.3048, thickness = 0.202 / 0.3048, height = 2.7 / 0.3048;
        // The solid is 0.202 m thick, the layers of the type add up to 0.25 m: the gross quantities follow the layers,
        // the net volume of the arc wall stays the footprint of the solid width times the height.
        var element = new Element { Id = 11, CategoryId = -2000011, SerializedSystemTypeId = 12, Geometry = Box(length, thickness, height),
            Location = new LocationCurve { CurveType = "Arc", Length = 3.2 / 0.3048 } };
        var elements = new Dictionary<int, Element> { [11] = element };
        if (withTypeLayers) elements[12] = WallTypeWithLayers(12, 0.1, 0.15);

        Apply(w, owner, elements, new() { [wall] = 11 });

        double width = withTypeLayers ? 0.25 : 0.202, side = 3.2 * 2.7;
        Assert.Equal(width, Quantities(w, "IFCQUANTITYLENGTH").Single(q => q.Name == "Width").Value, 9);
        AssertQuantities(Quantities(w, "IFCQUANTITYAREA"), ("GrossFootprintArea", 3.2 * width), ("GrossSideArea", side), ("NetSideArea", side));
        AssertQuantities(Quantities(w, "IFCQUANTITYVOLUME"), ("GrossVolume", width * side), ("NetVolume", 1.74528));
    }

    [Fact]
    public void Pass_SimpleRectangularWall_TypeWithoutUsableLayersKeepsTheProfileWidth()
    {
        var (w, owner, placement) = NewWriter();
        int shape = AddExtrusionBody(w, 3.2, 0.202, 2.7);
        int wall = AddProduct(w, "IFCWALLSTANDARDCASE", "CZGD7y6S5209$mGLi_sPlj", owner, placement, shape);
        double length = 3.2 / 0.3048, thickness = 0.202 / 0.3048, height = 2.7 / 0.3048;
        var element = new Element { Id = 11, CategoryId = -2000011, SerializedSystemTypeId = 12, Geometry = Box(length, thickness, height) };
        var type = WallTypeWithLayers(12, 0.0, double.NaN);

        Apply(w, owner, new() { [11] = element, [12] = type }, new() { [wall] = 11 });

        AssertQuantities(Quantities(w, "IFCQUANTITYLENGTH"), ("Height", 2.7), ("Length", 3.2), ("Width", 0.202));
    }

    [Fact]
    public void Pass_WallThatIsNotSimpleRectangular_UsesTheHeightAsItsLength()
    {
        var (w, owner, placement) = NewWriter();
        int shape = AddExtrusionBody(w, 5, 0.3, 3);
        int wall = AddProduct(w, "IFCWALLSTANDARDCASE", "DZGD7y6S5209$mGLi_sPlj", owner, placement, shape);
        // An L-shaped footprint whose vertical faces do not cancel in pairs by area; the solid is 3 m high like the written extrusion.
        var element = new Element { Id = 12, CategoryId = -2000011, Geometry = PrismOver([(0, 0), (5, 0), (5, 1), (2, 1), (2, 3), (0, 3)], 3 / 0.3048) };

        Apply(w, owner, new() { [12] = element }, new() { [wall] = 12 });

        Assert.Equal(3.0, Quantities(w, "IFCQUANTITYLENGTH").Single(q => q.Name == "Length").Value, 9);
        Assert.Equal(3.0, Quantities(w, "IFCQUANTITYLENGTH").Single(q => q.Name == "Height").Value, 9);
    }

    [Fact]
    public void Pass_WallThatIsNotSimpleRectangular_HeightAndLengthAreTheZRangeOfItsSolidNotTheWrittenDepth()
    {
        var (w, owner, placement) = NewWriter();
        // The extrusion that is written first is 3.3 m high (the height parameter of the wall) and the solid is 3 m high:
        // Height and, for the extrusion of the body exporter, Length are the 3 m of the solid.
        int shape = AddExtrusionBody(w, 5, 0.3, 3.3);
        int wall = AddProduct(w, "IFCWALLSTANDARDCASE", "DZGD7y6S5209$mGLi_sPlj", owner, placement, shape);
        var element = new Element { Id = 12, CategoryId = -2000011, Geometry = PrismOver([(0, 0), (5, 0), (5, 1), (2, 1), (2, 3), (0, 3)], 3 / 0.3048) };

        Apply(w, owner, new() { [12] = element }, new() { [wall] = 12 });

        var lengths = Quantities(w, "IFCQUANTITYLENGTH");
        Assert.Equal(3.0, lengths.Single(q => q.Name == "Height").Value, 9);
        Assert.Equal(3.0, lengths.Single(q => q.Name == "Length").Value, 9);
    }

    [Fact]
    public void Pass_WallWhoseInactiveFacesMakeItNotSimple_HeightAndLengthAreTheZRangeOfItsSolid()
    {
        var (w, owner, placement) = NewWriter();
        // A classifier-only face with a tilted normal on the node of the solid makes the wall not simple rectangular; the body
        // exporter writes it (3.3 m extrusion, 3 m solid) and Height and Length are still the Z range of the solid.
        int shape = AddExtrusionBody(w, 5, 0.3, 3.3);
        int wall = AddProduct(w, "IFCWALLSTANDARDCASE", "DZGD7y6S5209$mGLi_sPlj", owner, placement, shape);
        var geometry = Box(5 / 0.3048, 0.3 / 0.3048, 3 / 0.3048);
        foreach (var face in geometry.Faces) face.GeometryNode = 7;
        geometry.InactiveFaces = [new() { GeometryNode = 7, Normal = [1, 1, 1] }];
        var element = new Element { Id = 12, CategoryId = -2000011, Geometry = geometry };

        Apply(w, owner, new() { [12] = element }, new() { [wall] = 12 });

        var lengths = Quantities(w, "IFCQUANTITYLENGTH");
        Assert.Equal(3.0, lengths.Single(q => q.Name == "Height").Value, 9);
        Assert.Equal(3.0, lengths.Single(q => q.Name == "Length").Value, 9);
    }

    [Fact]
    public void Pass_WallOfSeveralSolids_HeightIsTheZRangeOfItsFirstSolid()
    {
        var (w, owner, placement) = NewWriter();
        // A standard-case wall of several solids is the extrusion of its first solid: a taller second solid does not count.
        int shape = AddExtrusionBody(w, 5, 0.3, 3.3);
        int wall = AddProduct(w, "IFCWALLSTANDARDCASE", "DZGD7y6S5209$mGLi_sPlj", owner, placement, shape);
        var geometry = PrismOver([(0, 0), (5, 0), (5, 1), (2, 1), (2, 3), (0, 3)], 3 / 0.3048);
        var second = PrismOver(Rectangle(1, 1), 20);
        int first = geometry.Faces.Count;
        geometry.Faces.AddRange(second.Faces);
        geometry.BrepSolids!.Add(new GeoBrepSolid { IsClosed = true, FaceIndices = Enumerable.Range(first, second.Faces.Count).ToList() });
        var element = new Element { Id = 12, CategoryId = -2000011, Geometry = geometry };

        Apply(w, owner, new() { [12] = element }, new() { [wall] = 12 });

        Assert.Equal(3.0, Quantities(w, "IFCQUANTITYLENGTH").Single(q => q.Name == "Height").Value, 9);
    }

    [Fact]
    public void Pass_Slab_GrossAndNetAreaAreTheTopSurfaceOfTheVerticalPrism()
    {
        var (w, owner, placement) = NewWriter();
        int slab = AddProduct(w, "IFCSLAB", "EZGD7y6S5209$mGLi_sPlj", owner, placement);
        var element = new Element { Id = 13, CategoryId = -2000032, Geometry = PrismOver(Rectangle(6, 3), 0.5) };

        Apply(w, owner, new() { [13] = element }, new() { [slab] = 13 });

        // No computed host area: the top surface is the profile of the prism.
        AssertQuantities(Quantities(w, "IFCQUANTITYAREA"), ("GrossArea", 18 * SquareFeet), ("NetArea", 18 * SquareFeet));
        AssertQuantities(Quantities(w, "IFCQUANTITYVOLUME"), ("GrossVolume", 18 * SquareFeet * 0.5 * 0.3048), ("NetVolume", 9 * CubicFeet));
        AssertQuantities(Quantities(w, "IFCQUANTITYLENGTH"), ("Perimeter", 18 * 0.3048), ("Width", 6 * 0.3048));
        Assert.StartsWith($"'{IfcGuid.FromMd5("IfcElementQuantity:QuantitySet: BaseQuantities:EZGD7y6S5209$mGLi_sPlj")}'", Assert.Single(Args(w, "IFCELEMENTQUANTITY")));

        var (w2, owner2, placement2) = NewWriter();
        int slab2 = AddProduct(w2, "IFCSLAB", "FZGD7y6S5209$mGLi_sPlj", owner2, placement2);
        var withArea = new Element { Id = 14, Geometry = PrismOver(Rectangle(6, 3), 0.5), Parameters = [Param(-1012805, 17.5)] };
        Apply(w2, owner2, new() { [14] = withArea }, new() { [slab2] = 14 });
        // The top surface of the solid comes before a computed host area derived from the sketch.
        AssertQuantities(Quantities(w2, "IFCQUANTITYAREA"), ("GrossArea", 18 * SquareFeet), ("NetArea", 18 * SquareFeet));
    }

    [Fact]
    public void Pass_FoundationPile_UsesCapQuadratureAreaAndTheVolumeOfTheCapNode()
    {
        var (w, owner, placement) = NewWriter();
        int slab = AddProduct(w, "IFCSLAB", "1bOeIks4fCNAuMrZZw4irf", owner, placement);
        double capArea = 0.7607188409331377;
        var pile = new Element
        {
            Id = 512279,
            CategoryId = -2001300,
            Geometry = new GeometryData
            {
                Faces =
                [
                    new GeoFace { QuadratureArea = capArea, PlaneSurface = true, OuterVertices = [[0, 0, 0]], GeometryNode = 7 }
                ],
                NodeMeasures = [new RvtIO.Parsing.MeshNodeMeasure(7, 62.41021102598124, 14.981675720484212)]
            }
        };

        Apply(w, owner, new() { [512279] = pile }, new() { [slab] = 512279 });

        double areaM2 = capArea * SquareFeet;
        double volM3 = 14.981675720484212 * CubicFeet;
        AssertQuantities(Quantities(w, "IFCQUANTITYAREA"), ("GrossArea", areaM2), ("NetArea", areaM2));
        AssertQuantities(Quantities(w, "IFCQUANTITYVOLUME"), ("NetVolume", volM3));
        Assert.Empty(Quantities(w, "IFCQUANTITYLENGTH"));
    }

    [Fact]
    public void Pass_Foundation_SumsTheVolumeOfEveryMeasuredNode()
    {
        var (w, owner, placement) = NewWriter();
        int slab = AddProduct(w, "IFCSLAB", "1bOeIks4fCNAuMrZZw4irf", owner, placement);
        var foundation = new Element
        {
            Id = 512280,
            CategoryId = -2001300,
            Geometry = new GeometryData
            {
                Faces =
                [
                    new GeoFace { QuadratureArea = 1, PlaneSurface = true, OuterVertices = [[0, 0, 0]], GeometryNode = 1 },
                    new GeoFace { PlaneSurface = true, OuterVertices = [[5, 0, 0]], GeometryNode = 2 }
                ],
                NodeMeasures = [new RvtIO.Parsing.MeshNodeMeasure(1, 6, 1), new RvtIO.Parsing.MeshNodeMeasure(2, 6, 1)]
            }
        };

        Apply(w, owner, new() { [512280] = foundation }, new() { [slab] = 512280 });

        AssertQuantities(Quantities(w, "IFCQUANTITYVOLUME"), ("NetVolume", 2 * CubicFeet));
    }

    [Fact]
    public void Pass_Foundation_AddsTheClosedShellOfASolidWithoutIntegrals()
    {
        var (w, owner, placement) = NewWriter();
        int slab = AddProduct(w, "IFCSLAB", "1bOeIks4fCNAuMrZZw4irf", owner, placement);
        var geometry = Box(1, 1, 1);
        foreach (var face in geometry.Faces) face.GeometryNode = 2;
        geometry.Faces.Add(new GeoFace { QuadratureArea = 1, PlaneSurface = true, OuterVertices = [[9, 0, 0]], GeometryNode = 1 });
        geometry.BrepSolids.Add(new GeoBrepSolid { IsClosed = false, FaceIndices = [6] });
        geometry.NodeMeasures = [new RvtIO.Parsing.MeshNodeMeasure(1, 6, 1)];
        var foundation = new Element { Id = 512281, CategoryId = -2001300, Geometry = geometry };

        Apply(w, owner, new() { [512281] = foundation }, new() { [slab] = 512281 });

        Assert.Equal(2 * CubicFeet, Quantities(w, "IFCQUANTITYVOLUME").Single(q => q.Name == "NetVolume").Value, 9);
    }

    [Fact]
    public void Pass_Foundation_FirstFaceInAnUnmeasuredNodeStillGetsTheMeasuredNodeVolume()
    {
        var (w, owner, placement) = NewWriter();
        int slab = AddProduct(w, "IFCSLAB", "1bOeIks4fCNAuMrZZw4irf", owner, placement);
        var geometry = new GeometryData
        {
            Faces =
            [
                new GeoFace { QuadratureArea = 1, PlaneSurface = true, OuterVertices = [[0, 0, 0]], GeometryNode = 1 },
                new GeoFace { PlaneSurface = true, OuterVertices = [[5, 0, 0]], GeometryNode = 2 }
            ],
            NodeMeasures = [new RvtIO.Parsing.MeshNodeMeasure(2, 6, 2)]
        };
        var foundation = new Element { Id = 512282, CategoryId = -2001300, Geometry = geometry };

        Apply(w, owner, new() { [512282] = foundation }, new() { [slab] = 512282 });

        Assert.Equal(2 * CubicFeet, Quantities(w, "IFCQUANTITYVOLUME").Single(q => q.Name == "NetVolume").Value, 9);
    }

    [Fact]
    public void Pass_Foundation_IgnoresAClosedShellOfANegativeNode()
    {
        var (w, owner, placement) = NewWriter();
        int slab = AddProduct(w, "IFCSLAB", "1bOeIks4fCNAuMrZZw4irf", owner, placement);
        var geometry = Box(1, 1, 1);
        foreach (var face in geometry.Faces) face.GeometryNode = -1;
        geometry.Faces.Add(new GeoFace { QuadratureArea = 1, PlaneSurface = true, OuterVertices = [[9, 0, 0]], GeometryNode = 1 });
        geometry.BrepSolids.Add(new GeoBrepSolid { IsClosed = false, FaceIndices = [6] });
        geometry.NodeMeasures = [new RvtIO.Parsing.MeshNodeMeasure(1, 6, 1)];
        var foundation = new Element { Id = 512283, CategoryId = -2001300, Geometry = geometry };

        Apply(w, owner, new() { [512283] = foundation }, new() { [slab] = 512283 });

        Assert.Equal(1 * CubicFeet, Quantities(w, "IFCQUANTITYVOLUME").Single(q => q.Name == "NetVolume").Value, 9);
    }

    [Fact]
    public void Pass_FoundationPile_WithoutAMeasuredNodeOrAClosedShellHasNoVolume()
    {
        var (w, owner, placement) = NewWriter();
        int slab = AddProduct(w, "IFCSLAB", "1bOeIks4fCNAuMrZZw4irf", owner, placement);
        var pile = new Element
        {
            Id = 512279,
            CategoryId = -2001300,
            Geometry = new GeometryData { Faces = [new GeoFace { QuadratureArea = 0.76, PlaneSurface = true, OuterVertices = [[0, 0, 0]] }] }
        };

        Apply(w, owner, new() { [512279] = pile }, new() { [slab] = 512279 });

        AssertQuantities(Quantities(w, "IFCQUANTITYAREA"), ("GrossArea", 0.76 * SquareFeet), ("NetArea", 0.76 * SquareFeet));
        Assert.Empty(Quantities(w, "IFCQUANTITYVOLUME"));
    }

    /// <summary>A closed box of a x b x c whose top is shifted by <paramref name="shift"/> along X: a sloped solid, no vertical extrusion.</summary>
    private static GeometryData SlopedBox(double a, double b, double c, double shift) => new()
    {
        Faces =
        [
            Face([0, 0, 0], [0, b, 0], [a, b, 0], [a, 0, 0]),
            Face([shift, 0, c], [a + shift, 0, c], [a + shift, b, c], [shift, b, c]),
            Face([0, 0, 0], [a, 0, 0], [a + shift, 0, c], [shift, 0, c]),
            Face([0, b, 0], [shift, b, c], [a + shift, b, c], [a, b, 0]),
            Face([0, 0, 0], [shift, 0, c], [shift, b, c], [0, b, 0]),
            Face([a, 0, 0], [a, b, 0], [a + shift, b, c], [a + shift, 0, c]),
        ],
        BrepSolids = [new GeoBrepSolid { IsClosed = true, FaceIndices = [0, 1, 2, 3, 4, 5] }],
    };

    [Fact]
    public void Pass_SlopedFoundation_NetVolumeIsTheVolumeOfItsSolid()
    {
        var (w, owner, placement) = NewWriter();
        int slab = AddProduct(w, "IFCSLAB", "2bOeIks4fCNAuMrZZw4irf", owner, placement);
        var geometry = SlopedBox(2, 1, 0.5, 3);
        geometry.Faces[0].QuadratureArea = 2;
        var foundation = new Element { Id = 41, CategoryId = -2001300, Geometry = geometry };

        Apply(w, owner, new() { [41] = foundation }, new() { [slab] = 41 });

        // the volume of the box (1 ft3), not the area of its first face times a length
        AssertQuantities(Quantities(w, "IFCQUANTITYVOLUME"), ("NetVolume", 1 * CubicFeet));
    }

    [Fact]
    public void Pass_SlopedFoundation_NetVolumeIsTheMeasureOfTheNodeOfTheCapFace()
    {
        var (w, owner, placement) = NewWriter();
        int slab = AddProduct(w, "IFCSLAB", "3bOeIks4fCNAuMrZZw4irf", owner, placement);
        var geometry = SlopedBox(2, 1, 0.5, 3);
        foreach (var face in geometry.Faces) face.GeometryNode = 5;
        geometry.Faces[0].QuadratureArea = 2;
        geometry.NodeMeasures = [new RvtIO.Parsing.MeshNodeMeasure(5, 9, 1.25)];
        var foundation = new Element { Id = 42, CategoryId = -2001300, Geometry = geometry };

        Apply(w, owner, new() { [42] = foundation }, new() { [slab] = 42 });

        AssertQuantities(Quantities(w, "IFCQUANTITYVOLUME"), ("NetVolume", 1.25 * CubicFeet));
    }

    [Fact]
    public void Pass_Column_ReproducesTheUnitSlipOfCreateBeamColumnBaseQuantities()
    {
        var (w, owner, placement) = NewWriter();
        int column = AddProduct(w, "IFCCOLUMN", "GZGD7y6S5209$mGLi_sPlj", owner, placement);
        var element = new Element { Id = 15, CategoryId = -2001330, Geometry = Box(1, 2, 5) };

        Apply(w, owner, new() { [15] = element }, new() { [column] = 15 });

        double length = 5 * 0.3048, area = 2 * SquareFeet, perimeter = 6 * 0.3048;
        AssertQuantities(Quantities(w, "IFCQUANTITYLENGTH"), ("Length", length));
        // The length and the perimeter in millimetres are multiplied and taken as square metres; the area stays in square metres.
        AssertQuantities(Quantities(w, "IFCQUANTITYAREA"), ("CrossSectionArea", area), ("OuterSurfaceArea", length * 1000 * perimeter * 1000));
        AssertQuantities(Quantities(w, "IFCQUANTITYVOLUME"), ("GrossVolume", length * 1000 * area), ("NetVolume", 10 * CubicFeet));
        Assert.StartsWith($"'{IfcGuid.FromMd5("IfcElementQuantity:BaseQuantities:GZGD7y6S5209$mGLi_sPlj")}'", Assert.Single(Args(w, "IFCELEMENTQUANTITY")));
    }

    [Theory]
    [InlineData("autodesk.unit.unit:meters-1.0.1", 0.3048)]
    [InlineData("autodesk.unit.unit:feet-1.0.1", 1)]
    public void Export_Column_InAMetreOrFootFile_MultipliesTheLengthOfTheFileUnitWithoutTheMillimetreSlip(string lengthUnit, double unitsPerFoot)
    {
        string ifc = ExportBeam(new IfcExporter(), lengthUnit: lengthUnit, column: true);

        double length = QuantityIn(ifc, "LENGTH", "Length"), area = QuantityIn(ifc, "AREA", "CrossSectionArea");
        Assert.Equal(10 * unitsPerFoot, length, 9);
        Assert.Equal(0.4 * 0.6 * unitsPerFoot * unitsPerFoot, area, 9);
        Assert.Equal(length * 2 * (0.4 + 0.6) * unitsPerFoot, QuantityIn(ifc, "AREA", "OuterSurfaceArea"), 9);
        Assert.Equal(length * area, QuantityIn(ifc, "VOLUME", "GrossVolume"), 9);
    }

    [Fact]
    public void Pass_Beam_UsesConsistentUnitsAndAddsTheSurfaces()
    {
        var (w, owner, placement) = NewWriter();
        int beam = AddProduct(w, "IFCBEAM", "HZGD7y6S5209$mGLi_sPlj", owner, placement);
        var element = new Element { Id = 16, CategoryId = -2001320, Geometry = Box(1, 2, 5) };

        Apply(w, owner, new() { [16] = element }, new() { [beam] = 16 });

        double length = 5 * 0.3048, area = 2 * SquareFeet, perimeter = 6 * 0.3048;
        AssertQuantities(Quantities(w, "IFCQUANTITYAREA"),
            ("CrossSectionArea", area), ("OuterSurfaceArea", perimeter * length), ("GrossSurfaceArea", perimeter * length + 2 * area),
            ("NetSurfaceArea", 2 * (1 * 2 + 1 * 5 + 2 * 5) * SquareFeet));
        AssertQuantities(Quantities(w, "IFCQUANTITYVOLUME"), ("GrossVolume", area * length), ("NetVolume", 10 * CubicFeet));
    }

    /// <summary>A beam whose Body the exporter writes as the extrusion of a prism (feet, along z) over the outline; the end face the
    /// profile is taken from holds the circular edges of the outline, when they are known.</summary>
    private static (StepWriter W, int Owner, int Beam) BeamOverOutline((double X, double Y)[] outline, double height,
        IReadOnlyList<RvtIO.Internal.Arc>? arcs, bool lowerFaceTurned = false)
    {
        var (w, owner, placement) = NewWriter();
        var geometry = PrismOver(outline, height);
        geometry.Faces[0].MeshArcs = arcs;
        if (lowerFaceTurned) geometry.Faces[0].OuterVertices!.Reverse();
        Assert.True(new IfcExporter().TryCreateOthersCurrentProductSweptSolid(w, geometry, 1, IfcExporter.OthersCurrentAxesZ, "P", out int shape, out _));
        Assert.Single(Args(w, "IFCARBITRARYCLOSEDPROFILEDEF"));
        return (w, owner, AddProduct(w, "IFCBEAM", "IZGD7y6S5209$mGLi_sPlj", owner, placement, shape));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void Pass_BeamWithArcsInItsProfile_MeasuresTheSectionWithTheArcsNotTheirChords(bool arcsKnown, bool lowerFaceTurned)
    {
        // An angle section (legs a, thickness t) with a concave root fillet r and a convex rounded heel R, each a quarter turn of 8 chords.
        const double a = 1, t = 0.1, r = 0.06, R = 0.04, height = 10, feet = 0.3048;
        var outline = new List<(double X, double Y)> { (R, 0), (a, 0), (a, t), (t + r, t) };
        for (int k = 1; k <= 8; k++)
            outline.Add((t + r + r * Math.Cos(-Math.PI / 2 - k * Math.PI / 16), t + r + r * Math.Sin(-Math.PI / 2 - k * Math.PI / 16)));
        outline.AddRange([(t, a), (0, a), (0, R)]);
        for (int k = 1; k < 8; k++)
            outline.Add((R + R * Math.Cos(Math.PI + k * Math.PI / 16), R + R * Math.Sin(Math.PI + k * Math.PI / 16)));
        // The two circular edges as the file stores them: the fillet runs against the outline, the heel with it.
        RvtIO.Internal.Arc[] arcs =
        [
            new([t + r, t + r, 0], [1, 0, 0], [0, 1, 0], r, Math.PI, 3 * Math.PI / 2),
            new([R, R, 0], [1, 0, 0], [0, 1, 0], R, Math.PI, 3 * Math.PI / 2),
        ];
        var (w, owner, beam) = BeamOverOutline(outline.ToArray(), height, arcsKnown ? arcs : null, lowerFaceTurned);
        var element = new Element { Id = 17, CategoryId = -2001320, Geometry = Box(1, 2, 5) };

        Apply(w, owner, new() { [17] = element }, new() { [beam] = 17 });

        // With the arcs the quarter circles themselves; without them the same points are a polygon and nothing else.
        double quarterDisc = arcsKnown ? Math.PI / 4 : 4 * Math.Sin(Math.PI / 16);
        double quarterArc = arcsKnown ? Math.PI / 2 : 16 * Math.Sin(Math.PI / 32);
        double area = (a * t + (a - t) * t + (1 - quarterDisc) * (r * r - R * R)) * SquareFeet;
        double perimeter = (4 * a - (2 - quarterArc) * (r + R)) * feet, depth = height * feet;
        AssertQuantities(Quantities(w, "IFCQUANTITYAREA"),
            ("CrossSectionArea", area), ("OuterSurfaceArea", perimeter * depth), ("GrossSurfaceArea", perimeter * depth + 2 * area),
            ("NetSurfaceArea", 2 * (1 * 2 + 1 * 5 + 2 * 5) * SquareFeet));
        AssertQuantities(Quantities(w, "IFCQUANTITYVOLUME"), ("GrossVolume", area * depth), ("NetVolume", 10 * CubicFeet));
    }

    [Theory]
    [InlineData(Math.PI / 20, 5)]
    [InlineData(Math.PI / 10, 6)]
    public void Pass_BeamWithAShallowArcInItsProfile_MeasuresAnArcWrittenAsOneOrTwoChords(double sweep, int writtenPoints)
    {
        // A plate with one corner cut off whose upper side, from (c, h) to (b, h), is an arc that bulges out.
        const double b = 1, h = 0.5, c = 0.2, height = 10, feet = 0.3048;
        double radius = (b - c) / (2 * Math.Sin(sweep / 2));
        var arc = new RvtIO.Internal.Arc([(b + c) / 2, h - radius * Math.Cos(sweep / 2), 0], [1, 0, 0], [0, 1, 0], radius,
            Math.PI / 2 - sweep / 2, Math.PI / 2 + sweep / 2);
        var (w, owner, beam) = BeamOverOutline([(0, 0), (b, 0), (b, h), (c, h), (0, h - c)], height, [arc]);
        Assert.Equal(writtenPoints + 1, IfcExporter.SplitStepArguments(Assert.Single(Args(w, "IFCPOLYLINE"))[1..^1]).Length);
        var element = new Element { Id = 17, CategoryId = -2001320, Geometry = Box(1, 2, 5) };

        Apply(w, owner, new() { [17] = element }, new() { [beam] = 17 });

        double area = (b * h - c * c / 2 + radius * radius / 2 * (sweep - Math.Sin(sweep))) * SquareFeet;
        double perimeter = (b + h + radius * sweep + c * Math.Sqrt(2) + h - c) * feet, depth = height * feet;
        AssertQuantities(Quantities(w, "IFCQUANTITYAREA"),
            ("CrossSectionArea", area), ("OuterSurfaceArea", perimeter * depth), ("GrossSurfaceArea", perimeter * depth + 2 * area),
            ("NetSurfaceArea", 2 * (1 * 2 + 1 * 5 + 2 * 5) * SquareFeet));
        AssertQuantities(Quantities(w, "IFCQUANTITYVOLUME"), ("GrossVolume", area * depth), ("NetVolume", 10 * CubicFeet));
    }

    [Fact]
    public void Pass_BeamWithAnArcClosedByAStraightSide_AddsNoArcToTheOutlineAsWritten()
    {
        // A half disc held as four chords. Its straight side joins the two ends of the arc as well, and the chords of the arc are
        // written over that side: the way the arc was laid is not a known one, so the section is the polygon as written.
        const double radius = 0.5, height = 10, feet = 0.3048;
        var outline = Enumerable.Range(0, 5).Select(k => (radius * Math.Cos(k * Math.PI / 4), radius * Math.Sin(k * Math.PI / 4))).ToArray();
        var (w, owner, beam) = BeamOverOutline(outline, height, [new([0, 0, 0], [1, 0, 0], [0, 1, 0], radius, 0, Math.PI)]);
        Assert.Empty(w.PolylineArcs);
        var element = new Element { Id = 17, CategoryId = -2001320, Geometry = Box(1, 2, 5) };

        Apply(w, owner, new() { [17] = element }, new() { [beam] = 17 });

        double area = radius * radius / 2 * (16 * Math.Sin(Math.PI / 16) - 4 * Math.Sin(Math.PI / 4)) * SquareFeet;
        double perimeter = 2 * radius * (4 * Math.Sin(Math.PI / 8) + 16 * Math.Sin(Math.PI / 32)) * feet, depth = height * feet;
        AssertQuantities(Quantities(w, "IFCQUANTITYAREA"),
            ("CrossSectionArea", area), ("OuterSurfaceArea", perimeter * depth), ("GrossSurfaceArea", perimeter * depth + 2 * area),
            ("NetSurfaceArea", 2 * (1 * 2 + 1 * 5 + 2 * 5) * SquareFeet));
        AssertQuantities(Quantities(w, "IFCQUANTITYVOLUME"), ("GrossVolume", area * depth), ("NetVolume", 10 * CubicFeet));
    }

    [Fact]
    public void Pass_BeamWithARegularPolygonProfile_MeasuresThePolygon()
    {
        var (w, owner, placement) = NewWriter();
        // A regular polygon of 32 straight sides: equal short chords on one circle, and no arc anywhere.
        const double radius = 0.1, depth = 3;
        const int sides = 32;
        var points = Enumerable.Range(0, sides)
            .Select(k => (X: radius * Math.Cos(2 * Math.PI * k / sides), Y: radius * Math.Sin(2 * Math.PI * k / sides)))
            .Select(p => w.Add("IFCCARTESIANPOINT", $"({StepWriter.Real(p.X)},{StepWriter.Real(p.Y)})")).ToList();
        int polyline = w.Add("IFCPOLYLINE", $"({string.Join(",", points.Append(points[0]).Select(id => "#" + id))})");
        int profile = w.Add("IFCARBITRARYCLOSEDPROFILEDEF", $".AREA.,'P',#{polyline}");
        int origin = w.Add("IFCCARTESIANPOINT", "(0.,0.,0.)");
        int position = w.Add("IFCAXIS2PLACEMENT3D", $"#{origin},$,$");
        int direction = w.Add("IFCDIRECTION", "(0.,0.,1.)");
        int solid = w.Add("IFCEXTRUDEDAREASOLID", $"#{profile},#{position},#{direction},{StepWriter.Real(depth)}");
        int representation = w.Add("IFCSHAPEREPRESENTATION", $"#1,'Body','SweptSolid',(#{solid})");
        int shape = w.Add("IFCPRODUCTDEFINITIONSHAPE", $"$,$,(#{representation})");
        int beam = AddProduct(w, "IFCBEAM", "IZGD7y6S5209$mGLi_sPlj", owner, placement, shape);
        var element = new Element { Id = 17, CategoryId = -2001320, Geometry = Box(1, 2, 5) };

        Apply(w, owner, new() { [17] = element }, new() { [beam] = 17 });

        double area = sides * radius * radius / 2 * Math.Sin(2 * Math.PI / sides);
        double perimeter = sides * 2 * radius * Math.Sin(Math.PI / sides);
        AssertQuantities(Quantities(w, "IFCQUANTITYAREA"),
            ("CrossSectionArea", area), ("OuterSurfaceArea", perimeter * depth), ("GrossSurfaceArea", perimeter * depth + 2 * area),
            ("NetSurfaceArea", 2 * (1 * 2 + 1 * 5 + 2 * 5) * SquareFeet));
        AssertQuantities(Quantities(w, "IFCQUANTITYVOLUME"), ("GrossVolume", area * depth), ("NetVolume", 10 * CubicFeet));
    }

    [Fact]
    public void ArcsOverChords_MeasuresTheArcsItIsGivenAndNoOther()
    {
        // 32 equal sides on the unit circle: nothing by themselves, the circle once they are the chords of one arc.
        var polygon = Enumerable.Range(0, 32).Select(k => (Math.Cos(k * Math.PI / 16), Math.Sin(k * Math.PI / 16))).ToList();
        Assert.Equal((0.0, 0.0), IfcExporter.ArcsOverChordsCurrent(polygon, []));

        var (area, length) = IfcExporter.ArcsOverChordsCurrent(polygon, [new(0, 32, 2 * Math.PI)]);
        Assert.Equal(Math.PI - 16 * Math.Sin(Math.PI / 16), area, 12);
        Assert.Equal(2 * Math.PI - 64 * Math.Sin(Math.PI / 32), length, 12);

        // A concave arc of four chords that runs over the first point: its segments are taken away, its length still added.
        var (concaveArea, concaveLength) = IfcExporter.ArcsOverChordsCurrent(polygon, [new(30, 4, -Math.PI / 4)]);
        Assert.Equal(2 * Math.Sin(Math.PI / 16) - Math.PI / 8, concaveArea, 12);
        Assert.Equal(Math.PI / 4 - 8 * Math.Sin(Math.PI / 32), concaveLength, 12);
    }

    /// <summary>A space placed in a storey (the storey has a placement of its own, the space is placed relative to it).</summary>
    private static (StepWriter W, int Owner, int Storey, int Space) SpaceInStorey()
    {
        var (w, owner, placement) = NewWriter();
        int storey = w.Add("IFCBUILDINGSTOREY", $"'SZGD7y6S5209$mGLi_sPlj',#{owner},'L1',$,$,#{placement},$,$,.ELEMENT.,0.");
        int axis = w.Add("IFCAXIS2PLACEMENT3D", "#2,$,$");
        int spacePlacement = w.Add("IFCLOCALPLACEMENT", $"#{placement},#{axis}");
        int space = AddProduct(w, "IFCSPACE", "IZGD7y6S5209$mGLi_sPlj", owner, spacePlacement);
        return (w, owner, storey, space);
    }

    [Fact]
    public void Pass_Space_TakesTheAreaFromTheRoomSolidAndTheHeightFromTheLevelOfTheRoom()
    {
        var (w, owner, storey, space) = SpaceInStorey();
        var room = new Element { Id = 17, CategoryId = -2000160, SerializedSpaceBoundary = Box(3, 3, 4) };

        Apply(w, owner, new() { [17] = room }, new() { [space] = 17 }, new() { [storey] = 3.05 });

        double area = 9 * SquareFeet;
        AssertQuantities(Quantities(w, "IFCQUANTITYAREA"), ("NetFloorArea", area), ("GrossFloorArea", area));
        // A box is an extrusion for the expected export: the outer perimeter of its end face (4 x 3 ft) is written after the height.
        AssertQuantities(Quantities(w, "IFCQUANTITYLENGTH"), ("Height", 3.05), ("GrossPerimeter", 12 * 0.3048));
        AssertQuantities(Quantities(w, "IFCQUANTITYVOLUME"), ("GrossVolume", area * 3.05));
    }

    [Fact]
    public void Pass_Space_TakesTheAreaFromThePlanCircuitInsteadOfThePolygonOfTheRoomSolid()
    {
        var (w, owner, storey, space) = SpaceInStorey();
        // The solid of a room with a curved wall is a polygon: the plan circuit gives the exact area (10 ft2 here, 9 for the box).
        var room = new Element
        {
            Id = 17, CategoryId = -2000160, SerializedSpaceBoundary = Box(3, 3, 4), SerializedRoomPlanAreaSquareFeet = 10,
        };

        Apply(w, owner, new() { [17] = room }, new() { [space] = 17 }, new() { [storey] = 3.05 });

        AssertQuantities(Quantities(w, "IFCQUANTITYAREA"), ("NetFloorArea", 10 * SquareFeet), ("GrossFloorArea", 10 * SquareFeet));
        AssertQuantities(Quantities(w, "IFCQUANTITYLENGTH"), ("Height", 3.05), ("GrossPerimeter", 12 * 0.3048));
        AssertQuantities(Quantities(w, "IFCQUANTITYVOLUME"), ("GrossVolume", 10 * SquareFeet * 3.05));
    }

    [Fact]
    public void Pass_Space_WithoutARoomSolidHasThePlanAreaButNoPerimeter()
    {
        var (w, owner, storey, space) = SpaceInStorey();
        var room = new Element { Id = 17, CategoryId = -2000160, SerializedRoomPlanAreaSquareFeet = 10 };

        Apply(w, owner, new() { [17] = room }, new() { [space] = 17 }, new() { [storey] = 3.05 });

        AssertQuantities(Quantities(w, "IFCQUANTITYAREA"), ("NetFloorArea", 10 * SquareFeet), ("GrossFloorArea", 10 * SquareFeet));
        AssertQuantities(Quantities(w, "IFCQUANTITYLENGTH"), ("Height", 3.05));
        AssertQuantities(Quantities(w, "IFCQUANTITYVOLUME"), ("GrossVolume", 10 * SquareFeet * 3.05));
    }

    [Fact]
    public void Pass_Space_WithoutAPlanArea_HasTheHeightOfItsStoreyOnly()
    {
        var (w, owner, storey, space) = SpaceInStorey();
        var room = new Element { Id = 17, CategoryId = -2000160 };

        Apply(w, owner, new() { [17] = room }, new() { [space] = 17 }, new() { [storey] = 3.05 });

        AssertQuantities(Quantities(w, "IFCQUANTITYLENGTH"), ("Height", 3.05));
        Assert.Empty(Quantities(w, "IFCQUANTITYAREA"));
        Assert.Empty(Quantities(w, "IFCQUANTITYVOLUME"));
    }

    [Fact]
    public void RoomExtrusionPerimeter_Box_IsTheOuterLoopOfTheBottomFace() =>
        Assert.Equal(12 * 0.3048, IfcExporter.RoomExtrusionPerimeterMetresCurrent(Box(3, 3, 4))!.Value, 12);

    [Fact]
    public void RoomExtrusionPerimeter_LPrism_IsThePerimeterOfTheL() =>
        Assert.Equal(16 * 0.3048,
            IfcExporter.RoomExtrusionPerimeterMetresCurrent(PrismOver([(0, 0), (4, 0), (4, 2), (2, 2), (2, 4), (0, 4)], 3))!.Value, 12);

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void RoomExtrusionPerimeter_ArcOfTheOutline_IsTheArcLengthNotItsChords(bool cylinderFacets)
    {
        var room = HalfDiscPrism(3, 6);
        if (!cylinderFacets) foreach (var facet in room.Faces) facet.AnalyticCylinderOrigin = null;
        // the diameter plus the half circle, or the diameter plus the six chords
        double expected = 6 + (cylinderFacets ? Math.PI * 3 : 6 * 2 * 3 * Math.Sin(Math.PI / 12));
        Assert.Equal(expected * 0.3048, IfcExporter.RoomExtrusionPerimeterMetresCurrent(room)!.Value, 9);
    }

    [Fact]
    public void RoomExtrusionPerimeter_SlopedCeiling_IsNoExtrusion()
    {
        // Floor 2 x 2 and a sloped top (heights 3 and 4 on opposite sides): no second horizontal face, one sloped face.
        var room = new GeometryData
        {
            Faces =
            [
                Face([0, 0, 0], [0, 2, 0], [2, 2, 0], [2, 0, 0]),
                Face([0, 0, 3], [2, 0, 3], [2, 2, 4], [0, 2, 4]),
                Face([0, 0, 0], [2, 0, 0], [2, 0, 3], [0, 0, 3]),
                Face([0, 2, 0], [0, 2, 4], [2, 2, 4], [2, 2, 0]),
                Face([0, 0, 0], [0, 0, 3], [0, 2, 4], [0, 2, 0]),
                Face([2, 0, 0], [2, 2, 0], [2, 2, 4], [2, 0, 3]),
            ],
        };

        Assert.Null(IfcExporter.RoomExtrusionPerimeterMetresCurrent(room));
    }

    [Fact]
    public void RoomExtrusionPerimeter_RoomTurnedAgainstTheModelAxes_IsNoExtrusion() =>
        Assert.Null(IfcExporter.RoomExtrusionPerimeterMetresCurrent(
            PrismOver([(0, 0), (2, 2), (0, 4), (-2, 2)], 3)));

    [Fact]
    public void RoomExtrusionPerimeter_FloorAndCeilingOfDifferentAreas_IsNoExtrusion()
    {
        var room = new GeometryData
        {
            Faces =
            [
                Face([0, 0, 0], [0, 3, 0], [3, 3, 0], [3, 0, 0]),
                Face([0, 0, 4], [2, 0, 4], [2, 3, 4], [0, 3, 4]),
            ],
        };

        Assert.Null(IfcExporter.RoomExtrusionPerimeterMetresCurrent(room));
    }

    [Fact]
    public void Pass_Railing_LengthIsTheLengthParameterOfTheRailing()
    {
        var (w, owner, placement) = NewWriter();
        int railing = AddProduct(w, "IFCRAILING", "RZGD7y6S5209$mGLi_sPlj", owner, placement);
        var element = new Element
        {
            Id = 60,
            Parameters = [new Parameter { Id = -1004005, Value = 12.5, StorageType = StorageType.Double, Definition = new Definition { Id = -1004005, Name = "CURVE_ELEM_LENGTH" } }],
        };

        Assert.Equal(1, Apply(w, owner, new() { [60] = element }, new() { [railing] = 60 }));

        AssertQuantities(Quantities(w, "IFCQUANTITYLENGTH"), ("Length", 12.5 * 0.3048));
        Assert.StartsWith($"'{IfcGuid.FromMd5("IfcElementQuantity:QuantitySet: BaseQuantities:RZGD7y6S5209$mGLi_sPlj")}'", Assert.Single(Args(w, "IFCELEMENTQUANTITY")));
    }

    [Fact]
    public void Pass_RailingWithoutALength_GetsNoSet()
    {
        var (w, owner, placement) = NewWriter();
        int railing = AddProduct(w, "IFCRAILING", "SZGD7y6S5209$mGLi_sPlj", owner, placement);

        Assert.Equal(0, Apply(w, owner, new() { [61] = new Element { Id = 61 } }, new() { [railing] = 61 }));
    }

    [Theory]
    [InlineData(false, 0.22)] [InlineData(true, 12.5 * 0.3048)]
    public void Pass_RailingOwnSymbolLengthIsTheFallbackAfterAnExplicitParameter(bool explicitLength, double expected)
    {
        var (w, owner, placement) = NewWriter();
        int railing = AddProduct(w, "IFCRAILING", "SZGD7y6S5209$mGLi_sPlj", owner, placement);
        var element = new Element { Id = 61, ValidatedRailingLengthFeet = 0.22 / 0.3048 };
        if (explicitLength) element.Parameters.Add(new Parameter { Id = -1004005, Value = 12.5,
            StorageType = StorageType.Double, Definition = new Definition { Id = -1004005, Name = "CURVE_ELEM_LENGTH" } });
        Assert.Equal(1, Apply(w, owner, new() { [61] = element }, new() { [railing] = 61 }));
        AssertQuantities(Quantities(w, "IFCQUANTITYLENGTH"), ("Length", expected));
    }

    [Fact]
    public void Pass_Space_WithoutALevelHeight_HasTheAreaOnly()
    {
        var (w, owner, _, space) = SpaceInStorey();
        var room = new Element { Id = 18, CategoryId = -2000160, SerializedSpaceBoundary = Box(3, 3, 4) };

        Apply(w, owner, new() { [18] = room }, new() { [space] = 18 }, new());

        AssertQuantities(Quantities(w, "IFCQUANTITYLENGTH"), ("GrossPerimeter", 12 * 0.3048));
        Assert.Empty(Quantities(w, "IFCQUANTITYVOLUME"));
        AssertQuantities(Quantities(w, "IFCQUANTITYAREA"), ("NetFloorArea", 9 * SquareFeet), ("GrossFloorArea", 9 * SquareFeet));
    }

    [Fact]
    public void Pass_Space_WithASplitCeiling_TakesTheLargerOfTheUpwardAndTheDownwardFaces()
    {
        var (w, owner, storey, space) = SpaceInStorey();
        // One floor (3 x 3, facing down) under a ceiling in two steps (3 x 2 at 4, 3 x 1 at 3.5, facing up).
        var room = new Element
        {
            Id = 19, CategoryId = -2000160,
            SerializedSpaceBoundary = new GeometryData
            {
                Faces =
                [
                    Face([0, 0, 0], [0, 3, 0], [3, 3, 0], [3, 0, 0]),
                    Face([0, 0, 4], [3, 0, 4], [3, 2, 4], [0, 2, 4]),
                    Face([0, 2, 3.5], [3, 2, 3.5], [3, 3, 3.5], [0, 3, 3.5]),
                ],
            },
        };

        Apply(w, owner, new() { [19] = room }, new() { [space] = 19 }, new() { [storey] = 3.0 });

        AssertQuantities(Quantities(w, "IFCQUANTITYAREA"), ("NetFloorArea", 9 * SquareFeet), ("GrossFloorArea", 9 * SquareFeet));
    }

    [Fact]
    public void Pass_Space_FacesAllWoundTheSameWay_StillGiveThePlanAreaOnceAndThePerimeter()
    {
        var (w, owner, storey, space) = SpaceInStorey();
        // Floor and ceiling both counterclockwise seen from above: the winding does not tell which one looks out.
        var room = new Element
        {
            Id = 23, CategoryId = -2000160,
            SerializedSpaceBoundary = new GeometryData
            {
                Faces =
                [
                    Face([0, 0, 0], [3, 0, 0], [3, 3, 0], [0, 3, 0]),
                    Face([0, 0, 4], [3, 0, 4], [3, 3, 4], [0, 3, 4]),
                    Face([0, 0, 0], [3, 0, 0], [3, 0, 4], [0, 0, 4]),
                ],
            },
        };

        Apply(w, owner, new() { [23] = room }, new() { [space] = 23 }, new() { [storey] = 3.0 });

        AssertQuantities(Quantities(w, "IFCQUANTITYAREA"), ("NetFloorArea", 9 * SquareFeet), ("GrossFloorArea", 9 * SquareFeet));
        AssertQuantities(Quantities(w, "IFCQUANTITYLENGTH"), ("Height", 3.0), ("GrossPerimeter", 12 * 0.3048));
    }

    [Fact]
    public void StoreyHeights_AreTheDistancesToTheNextStoreyUp_AndTheTopStoreyHasNone()
    {
        var heights = IfcExporter.StoreyHeightsCurrent(new Dictionary<int, double> { [1] = 0, [2] = 10, [3] = 10 + 1e-9, [4] = 30 });

        Assert.Equal(10 * 0.3048, heights[1], 12);
        Assert.Equal(20 * 0.3048, heights[2], 12);
        Assert.Equal(20 * 0.3048, heights[3], 6);
        Assert.False(heights.ContainsKey(4));
    }

    [Theory]
    [InlineData(true, "0.3", true)]
    [InlineData(false, "0.3", false)]
    [InlineData(true, "$", false)]
    public void Pass_StairFlight_LengthIsTreadsTimesTreadLengthOfARunThatTakesTheSurrogate(bool takesTheSurrogate, string treadLength, bool hasLength)
    {
        var (w, owner, placement) = NewWriter();
        int flight = w.Add("IFCSTAIRFLIGHT", $"'KZGD7y6S5209$mGLi_sPlj',#{owner},'Run',$,'Monolithic Run:190mm Depth',#{placement},$,'1',3,3,0.1805,{treadLength}");
        var element = new Element { Id = 19, CategoryId = -2000120, Geometry = Box(1, 1, 2) };
        var exporter = new IfcExporter();
        if (takesTheSurrogate) exporter.LengthRuleRunsCurrent[19] = 3;

        Apply(w, owner, new() { [19] = element }, new() { [flight] = 19 }, exporter: exporter);

        AssertQuantities(Quantities(w, "IFCQUANTITYVOLUME"), ("NetVolume", 2 * CubicFeet));
        if (hasLength) AssertQuantities(Quantities(w, "IFCQUANTITYLENGTH"), ("Length", 0.9));
        else Assert.Empty(Quantities(w, "IFCQUANTITYLENGTH"));
    }

    private static Element StairRunOf(int id, int stairsId, int topRiserIndex, int beginWithRiser, int endWithRiser, bool sketched) => new()
    {
        Id = id, CategoryId = -2000120, Geometry = Box(1, 1, 2),
        SerializedStairRunInput = new RvtIO.Parsing.TfBlockDeserializer.StairRunInput(id, stairsId,
            sketched ? RvtIO.Parsing.TfBlockDeserializer.StairRunStyle.Sketched : RvtIO.Parsing.TfBlockDeserializer.StairRunStyle.Straight, [new(1, 0, 0)],
            new RvtIO.Parsing.TfBlockDeserializer.StairRunNumbers(0.0, 1.5, 3.0, 0, topRiserIndex, beginWithRiser, endWithRiser)),
    };

    [Fact]
    public void Pass_StairFlight_AnAssembledRunWhoseAttributesAreSet_StillHasNoLength()
    {
        // A sketched run without an end riser, indices 0 to 17, and a stair that stores 16 risers: the attributes of the flight count 16 risers
        // and 16 treads and agree with the stair, the count the surrogate was calibrated on (17 risers) does not, so the run takes no Length.
        var stairs = new Element { Id = 100, SerializedActualNumberOfRisers = 16, SerializedActualTreadDepthFeet = 0.8202099737532809 };
        var run = StairRunOf(19, 100, topRiserIndex: 17, beginWithRiser: 1, endWithRiser: 0, sketched: true);
        var byId = new Dictionary<int, Element> { [100] = stairs, [19] = run };
        string attributes = IfcExporter.StairFlightAttributes(run, byId);
        Assert.StartsWith("16,16,", attributes);
        var (w, owner, placement) = NewWriter();
        int flight = w.Add("IFCSTAIRFLIGHT", $"'KZGD7y6S5209$mGLi_sPlj',#{owner},'Run',$,'Non-Monolithic Run:50mm Tread 13mm Riser',#{placement},$,'19',{attributes}");
        var exporter = new IfcExporter();
        // as the export does for a run that takes the surrogate
        if (IfcExporter.RunLengthTreadsCurrent(run, byId) is int treads) exporter.LengthRuleRunsCurrent[run.Id] = treads;
        Assert.Empty(exporter.LengthRuleRunsCurrent);

        Apply(w, owner, new() { [19] = run }, new() { [flight] = 19 }, exporter: exporter);

        AssertQuantities(Quantities(w, "IFCQUANTITYVOLUME"), ("NetVolume", 2 * CubicFeet));
        Assert.Empty(Quantities(w, "IFCQUANTITYLENGTH"));
    }

    [Theory]
    [InlineData(false, 1, 1, 4, 3)]   // a straight run with a riser at both ends: 4 risers, 3 treads
    [InlineData(true, 1, 1, 4, 3)]    // a sketched run that ends with a riser counts like it
    [InlineData(true, 1, 0, 4, 4)]    // a sketched run without an end riser, the stair storing the index span: all of it
    [InlineData(true, 1, 0, 3, -1)]   // ... but not when the stair stores the count that is one less
    [InlineData(false, 1, 0, 3, -1)]  // a straight run with the same flag counts its index span too
    [InlineData(true, 1, 0, -2, 4)]   // a stair that stores no riser count agrees with any run
    public void RunLengthTreads_AreCountedFromTheIndexSpan_AndOnlyWhereTheStairAgrees(bool sketched, int begin, int end, int storedRisers, int expected)
    {
        var stairs = new Element { Id = 100, SerializedActualNumberOfRisers = storedRisers == -2 ? null : storedRisers };
        var run = StairRunOf(19, 100, topRiserIndex: 4, begin, end, sketched);
        var byId = new Dictionary<int, Element> { [100] = stairs, [19] = run };

        int? treads = IfcExporter.RunLengthTreadsCurrent(run, byId);

        Assert.Equal(expected == -1 ? null : expected, treads);
    }

    [Fact]
    public void RunLengthTreads_NeedTheRunInputOfTheRunItself()
    {
        var byId = new Dictionary<int, Element> { [100] = new Element { Id = 100 } };
        Assert.Null(IfcExporter.RunLengthTreadsCurrent(new Element { Id = 19 }, byId));
        var other = StairRunOf(20, 100, topRiserIndex: 4, beginWithRiser: 1, endWithRiser: 1, sketched: false);
        Assert.Null(IfcExporter.RunLengthTreadsCurrent(new Element { Id = 19, SerializedStairRunInput = other.SerializedStairRunInput }, byId));
    }


    private static string ExportBeam(IfcExporter exporter, int? structuralType = null, bool geometryUndecoded = false, bool driveCurve = false,
        string? lengthUnit = null, bool column = false)
    {
        var beam = new Element
        {
            Id = 102,
            TypeId = 103,
            IsDiscovered = true,
            CategoryId = -2001320,
            Category = "OST_StructuralFraming",
            Name = "Nominal rectangular beam",
            References = [3],
            Parameters = [new Parameter { Id = -1012100, StorageType = StorageType.ElementId, Value = 3 }],
            BoundingBox = [1.8, 0, 3.7, 2.2, 10, 4.3],
            Geometry = new GeometryData
            {
                BoundingBox = [1.8, 0, 3.7, 2.2, 10, 4.3],
                Faces = Box(0.4, 10, 0.6).Faces.Select(face => new GeoFace
                {
                    OuterVertices = face.OuterVertices!.Select(p => new[] { p[0] + 1.8, p[1], p[2] + 3.7 }).ToList(),
                }).ToList(),
                BrepSolids = [new GeoBrepSolid { IsClosed = true, FaceIndices = [0, 1, 2, 3, 4, 5] }],
            },
        };
        beam.NativeStructuralType = structuralType;
        if (driveCurve)
        {
            beam.CurveDriverLineLength = 10;
            beam.CurveDriverLineZ = (0, 5);
        }
        if (column)
        {
            // the same box standing: 0.4 x 0.6 ft, 10 ft tall
            beam.CategoryId = -2001330;
            beam.Category = "OST_StructuralColumns";
            beam.BoundingBox = beam.Geometry.BoundingBox = [1.8, 0, 3.7, 2.2, 0.6, 13.7];
            beam.Geometry.Faces = Box(0.4, 0.6, 10).Faces.Select(face => new GeoFace
            {
                OuterVertices = face.OuterVertices!.Select(p => new[] { p[0] + 1.8, p[1], p[2] + 3.7 }).ToList(),
            }).ToList();
        }
        var info = new RevitFileInfo { GeometryUndecoded = geometryUndecoded, SerializedLengthUnit = lengthUnit };
        info.Elements.Add(beam);
        string path = Path.Combine(Path.GetTempPath(), $"revitreader-base-quantities-{Guid.NewGuid():N}.ifc");
        try
        {
            exporter.Export(info, path);
            return File.ReadAllText(path);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static double QuantityIn(string ifc, string type, string name) =>
        double.Parse(Regex.Match(ifc, $"=IFCQUANTITY{type}\\('{name}','',\\$,([^)]*)\\);").Groups[1].Value, CultureInfo.InvariantCulture);

    [Fact]
    public void Option_Off_NoQuantitySetIsWritten() =>
        Assert.DoesNotContain("IFCELEMENTQUANTITY", ExportBeam(new IfcExporter { BaseQuantities = false }));

    [Fact]
    public void Option_ByDefault_IsOnForCurrent_AndNotForLegacy()
    {
        Assert.True(new IfcExporter().BaseQuantities);
        Assert.Contains("IFCELEMENTQUANTITY", ExportBeam(new IfcExporter()));
        Assert.DoesNotContain("IFCELEMENTQUANTITY", ExportBeam(new IfcExporter { Profile = IfcProfile.Legacy }));
    }

    [Fact]
    public void Option_On_BeamGetsTheBeamDescriptionQuantitiesInMillimetresAndSquareMetres()
    {
        string ifc = ExportBeam(new IfcExporter { BaseQuantities = true });

        Assert.Single(Regex.Matches(ifc, "=IFCELEMENTQUANTITY\\("));
        Assert.Contains("'BaseQuantities','',$,(", ifc);
        Assert.Equal(10 * 0.3048 * 1000, QuantityIn(ifc, "LENGTH", "Length"), 6);
        Assert.Equal(0.4 * 0.6 * SquareFeet, QuantityIn(ifc, "AREA", "CrossSectionArea"), 9);
        Assert.Equal(0.4 * 0.6 * 10 * CubicFeet, QuantityIn(ifc, "VOLUME", "NetVolume"), 9);
    }

    [Fact]
    public void Option_On_BraceOfDecodedFile_IsAMemberWithTheBeamLengthAndCrossSectionAreaAndItsOwnSlope()
    {
        string ifc = ExportBeam(new IfcExporter { BaseQuantities = true }, structuralType: 2, driveCurve: true);

        Assert.Contains("=IFCMEMBER(", ifc);
        Assert.Equal(10 * 0.3048 * 1000, QuantityIn(ifc, "LENGTH", "Length"), 6);
        Assert.Equal(0.4 * 0.6 * SquareFeet, QuantityIn(ifc, "AREA", "CrossSectionArea"), 9);
        Assert.Equal(0.4 * 0.6 * 10 * CubicFeet, QuantityIn(ifc, "VOLUME", "NetVolume"), 9);
        // the rest is the member rule: no beam-only quantities
        Assert.DoesNotContain("'GrossVolume'", ifc);
        Assert.DoesNotContain("'OuterSurfaceArea'", ifc);
        // one Slope, the angle of the drive curve (rise 5 over length 10), in Pset_MemberCommon
        Assert.Single(Regex.Matches(ifc, "'Slope'"));
        Assert.Equal(30.0, double.Parse(Regex.Match(ifc, @"'Slope',[^,]*,IFCPLANEANGLEMEASURE\(([^)]+)\)").Groups[1].Value, CultureInfo.InvariantCulture), 9);
        Assert.DoesNotContain("Pset_BeamCommon", ifc);
    }

    [Fact]
    public void Option_On_BraceOfUndecodedFile_KeepsTheMemberRule()
    {
        string ifc = ExportBeam(new IfcExporter { BaseQuantities = true }, structuralType: 2, geometryUndecoded: true);

        Assert.Contains("=IFCMEMBER(", ifc);
        // the member rule reads half the surface area of the solid as the section
        Assert.NotEqual(0.4 * 0.6 * SquareFeet, QuantityIn(ifc, "AREA", "CrossSectionArea"), 6);
    }

    [Theory]
    [InlineData(55)]
    [InlineData(485508)]
    [InlineData(485509)]
    [InlineData(846334)]
    [InlineData(846335)]
    [InlineData(846351)]
    public void Pass_MullionInCurtainWall_WithSlantedEndFace_MeasuresBetweenEndFaceCentres(int elementId)
    {
        var (w, owner, placement) = NewWriter();
        int curtainWall = AddProduct(w, "IFCCURTAINWALL", "1ZGD7y6S5209$mGLi_sPlj", owner, placement);
        int mullion = AddProduct(w, "IFCMEMBER", "2ZGD7y6S5209$mGLi_sPlj", owner, placement);
        w.Add("IFCRELAGGREGATES", $"'3ZGD7y6S5209$mGLi_sPlj',#{owner},$,$,#{curtainWall},(#{mullion})");

        double u = 1.0 / Math.Sqrt(2);
        var elem = new Element
        {
            Id = elementId,
            CategoryId = -2000171,
            Transform = new InstanceTransform { BasisZ = [0, -1, 0] },
            Geometry = new GeometryData
            {
                Faces =
                [
                    new GeoFace { Normal = [0, 1, 0], OuterVertices = [[0, 10, 0], [1, 10, 0], [1, 10, 1], [0, 10, 1]] },
                    new GeoFace { Normal = [u, -u, 0], OuterVertices = [[0, 0, 0], [1, 1, 0], [1, 1, 1], [0, 0, 1]] },
                ],
                BrepSolids = [new GeoBrepSolid { IsClosed = true, FaceIndices = [0, 1] }]
            }
        };

        Apply(w, owner, new() { [elementId] = elem }, new() { [mullion] = elementId });

        AssertQuantities(Quantities(w, "IFCQUANTITYLENGTH"), ("Length", 9.5 * 0.3048));
    }

    [Fact]
    public void Pass_MullionInRoof_UsesBoundingBoxExtent_EvenWithSlantedEndFace()
    {
        var (w, owner, placement) = NewWriter();
        int roof = AddProduct(w, "IFCROOF", "1ZGD7y6S5209$mGLi_sPlj", owner, placement);
        int mullion = AddProduct(w, "IFCMEMBER", "2ZGD7y6S5209$mGLi_sPlj", owner, placement);
        w.Add("IFCRELAGGREGATES", $"'3ZGD7y6S5209$mGLi_sPlj',#{owner},$,$,#{roof},(#{mullion})");

        double u = 1.0 / Math.Sqrt(2);
        var elem = new Element
        {
            Id = 56,
            CategoryId = -2000171,
            Transform = new InstanceTransform { BasisZ = [0, -1, 0] },
            Geometry = new GeometryData
            {
                Faces =
                [
                    new GeoFace { Normal = [0, 1, 0], OuterVertices = [[0, 10, 0], [1, 10, 0], [1, 10, 1], [0, 10, 1]] },
                    new GeoFace { Normal = [u, -u, 0], OuterVertices = [[0, 0, 0], [1, 1, 0], [1, 1, 1], [0, 0, 1]] },
                ],
                BrepSolids = [new GeoBrepSolid { IsClosed = true, FaceIndices = [0, 1] }]
            }
        };

        Apply(w, owner, new() { [56] = elem }, new() { [mullion] = 56 });

        AssertQuantities(Quantities(w, "IFCQUANTITYLENGTH"), ("Length", 10.0 * 0.3048));
    }

    [Theory]
    [InlineData(15)]
    [InlineData(414482)]
    public void Pass_Column_UsesGeometryRegardlessOfElementId(int elementId)
    {
        var (w, owner, placement) = NewWriter();
        int column = AddProduct(w, "IFCCOLUMN", "1ZGD7y6S5209$mGLi_sPlk", owner, placement);
        var elem = new Element { Id = elementId, Geometry = Box(2, 2, 2) };

        Apply(w, owner, new() { [elementId] = elem }, new() { [column] = elementId });

        Assert.Equal(4 * SquareFeet, Quantities(w, "IFCQUANTITYAREA").Single(q => q.Name == "CrossSectionArea").Value, 9);
        Assert.Equal(8 * CubicFeet, Quantities(w, "IFCQUANTITYVOLUME").Single(q => q.Name == "NetVolume").Value, 9);
    }

    [Theory]
    [InlineData("Generic door")]
    [InlineData("Pocket_Slider_Door_5851")]
    public void Pass_Door_UsesStoredCoarseSurfaceAreaRegardlessOfFamilyName(string familyName)
    {
        var (w, owner, placement) = NewWriter();
        int door = AddProduct(w, "IFCDOOR", "2ZGD7y6S5209$mGLi_sPlk", owner, placement);
        var elem = new Element { Id = 505974, FamilyName = familyName, Geometry = Box(1, 1, 1), CoarseSurfaceArea = 12 };

        Apply(w, owner, new() { [505974] = elem }, new() { [door] = 505974 });

        AssertQuantities(Quantities(w, "IFCQUANTITYAREA"), ("Area", 6 * SquareFeet));
    }

    [Fact]
    public void Option_On_Legacy_NoQuantitySetIsWritten() =>
        Assert.DoesNotContain("IFCELEMENTQUANTITY", ExportBeam(new IfcExporter { BaseQuantities = true, Profile = IfcProfile.Legacy }));

    // ---- solids the exporter takes: the surface integrals of the taken Geometry nodes ----

    /// <summary>Two boxes, the first with the Geometry node <paramref name="taken"/> (a node the exporter takes) and the second with a negative id
    /// (a node it does not take); the shells of the second add to the polygon totals but not to the solids the exporter measures.</summary>
    private static GeometryData TakenAndUntakenBoxes(int taken, int untaken)
    {
        var first = Box(1, 2, 5);
        var second = Box(3, 3, 3);
        foreach (var face in first.Faces) face.GeometryNode = taken;
        foreach (var face in second.Faces) face.GeometryNode = untaken;
        var geometry = new GeometryData { Faces = [.. first.Faces, .. second.Faces] };
        geometry.BrepSolids =
        [
            new GeoBrepSolid { IsClosed = true, FaceIndices = [0, 1, 2, 3, 4, 5] },
            new GeoBrepSolid { IsClosed = true, FaceIndices = [6, 7, 8, 9, 10, 11] },
        ];
        return geometry;
    }

    [Theory]
    [InlineData(3, -4)]
    [InlineData(57, -2)]
    public void NodeSolidTotals_SumTheMeasuresOfTheTakenNodesWhateverTheirNumbers(int taken, int untaken)
    {
        var geometry = TakenAndUntakenBoxes(taken, untaken);
        geometry.NodeMeasures = [new RvtIO.Parsing.MeshNodeMeasure(taken, 40, 7)];

        var totals = BrepMeasures.NodeSolidTotalsCurrent(geometry);

        Assert.Equal(7 * CubicFeet, totals!.Value.Volume, 12);
        Assert.Equal(40 * SquareFeet, totals.Value.Area, 12);
    }

    [Fact]
    public void NodeSolidTotals_AreUnknownWhileATakenNodeHasNoMeasure()
    {
        var geometry = TakenAndUntakenBoxes(3, -4);
        geometry.NodeMeasures = [new RvtIO.Parsing.MeshNodeMeasure(9, 40, 7)];

        Assert.Null(BrepMeasures.NodeSolidTotalsCurrent(geometry));
        Assert.Null(BrepMeasures.NodeSolidTotalsCurrent(TakenAndUntakenBoxes(3, -4)));
    }

    [Theory]
    [InlineData(3, -4, 16, "Generic")]
    [InlineData(41, -8, 99, "S_G_H_3Sec")]
    public void Pass_Beam_NetVolumeAndNetSurfaceAreaAreThoseOfTheTakenNodesOnly(int taken, int untaken, int elementId, string family)
    {
        var (w, owner, placement) = NewWriter();
        int beam = AddProduct(w, "IFCBEAM", "HZGD7y6S5209$mGLi_sPlj", owner, placement);
        var geometry = TakenAndUntakenBoxes(taken, untaken);
        geometry.NodeMeasures = [new RvtIO.Parsing.MeshNodeMeasure(taken, 40, 7)];
        var element = new Element { Id = elementId, CategoryId = -2001320, FamilyName = family, Geometry = geometry };

        Apply(w, owner, new() { [elementId] = element }, new() { [beam] = elementId });

        var areas = Quantities(w, "IFCQUANTITYAREA");
        Assert.Equal(40 * SquareFeet, areas.Single(q => q.Name == "NetSurfaceArea").Value, 9);
        Assert.Equal(7 * CubicFeet, Quantities(w, "IFCQUANTITYVOLUME").Single(q => q.Name == "NetVolume").Value, 9);
        // the cross section stays the half surface of every solid of the geometry (the exporter's computed "Area")
        Assert.Equal((34 + 54) / 2.0 * SquareFeet, areas.Single(q => q.Name == "CrossSectionArea").Value, 9);
    }

    [Fact]
    public void Pass_Column_WithoutMeasuresKeepsTheShellTotals()
    {
        var (w, owner, placement) = NewWriter();
        int column = AddProduct(w, "IFCCOLUMN", "GZGD7y6S5209$mGLi_sPlj", owner, placement);
        var element = new Element { Id = 15, CategoryId = -2001330, Geometry = TakenAndUntakenBoxes(3, -4) };

        Apply(w, owner, new() { [15] = element }, new() { [column] = 15 });

        Assert.Equal((10 + 27) * CubicFeet, Quantities(w, "IFCQUANTITYVOLUME").Single(q => q.Name == "NetVolume").Value, 9);
    }

    private static int AddTwoExtrusionBody(StepWriter w, double x, double y, double firstDepth, double lastDepth)
    {
        int origin = w.Add("IFCCARTESIANPOINT", "(0.,0.)");
        int profilePlacement = w.Add("IFCAXIS2PLACEMENT2D", $"#{origin},$");
        int profile = w.Add("IFCRECTANGLEPROFILEDEF", $".AREA.,$,#{profilePlacement},{StepWriter.Real(x)},{StepWriter.Real(y)}");
        int point3 = w.Add("IFCCARTESIANPOINT", "(0.,0.,0.)");
        int position = w.Add("IFCAXIS2PLACEMENT3D", $"#{point3},$,$");
        int direction = w.Add("IFCDIRECTION", "(0.,0.,1.)");
        int first = w.Add("IFCEXTRUDEDAREASOLID", $"#{profile},#{position},#{direction},{StepWriter.Real(firstDepth)}");
        int last = w.Add("IFCEXTRUDEDAREASOLID", $"#{profile},#{position},#{direction},{StepWriter.Real(lastDepth)}");
        int context = w.Add("IFCGEOMETRICREPRESENTATIONCONTEXT", $"$,'Model',3,1.E-05,#{position},$");
        int representation = w.Add("IFCSHAPEREPRESENTATION", $"#{context},'Body','SweptSolid',(#{first},#{last})");
        return w.Add("IFCPRODUCTDEFINITIONSHAPE", $"$,$,(#{representation})");
    }

    [Fact]
    public void Pass_Column_TakesTheExtrusionDataOfTheLastOfSeveralExtrusions()
    {
        var (w, owner, placement) = NewWriter();
        int shape = AddTwoExtrusionBody(w, 0.5, 0.25, 1.5, 4.0);
        int column = AddProduct(w, "IFCCOLUMN", "GZGD7y6S5209$mGLi_sPlj", owner, placement, shape);
        var element = new Element { Id = 21, CategoryId = -2001330, Geometry = Box(1, 2, 5) };

        Apply(w, owner, new() { [21] = element }, new() { [column] = 21 });

        // The exporter keeps the extrusion data of the last body it exports
        Assert.Equal(4.0, Quantities(w, "IFCQUANTITYLENGTH").Single(q => q.Name == "Length").Value, 9);
        Assert.Equal(0.5 * 0.25, Quantities(w, "IFCQUANTITYAREA").Single(q => q.Name == "CrossSectionArea").Value, 9);
    }

    // ---- cutting nodes: closed nodes whose faces point into them (negative signed volume) are exported as any node, the exporter takes no solid from them ----

    private static GeometryData BoxesInNodes(int taken, int cutting)
    {
        var geometry = TakenAndUntakenBoxes(taken, cutting);
        geometry.NodeMeasures =
        [
            new RvtIO.Parsing.MeshNodeMeasure(taken, 40, 7),
            new RvtIO.Parsing.MeshNodeMeasure(cutting, 100, -9),
        ];
        return geometry;
    }

    [Theory]
    [InlineData(3, 4)]
    [InlineData(12, 5)]
    public void NodeSolidTotals_LeaveOutTheCuttingNodes(int taken, int cutting)
    {
        var totals = BrepMeasures.NodeSolidTotalsCurrent(BoxesInNodes(taken, cutting));

        Assert.Equal(7 * CubicFeet, totals!.Value.Volume, 12);
        Assert.Equal(40 * SquareFeet, totals.Value.Area, 12);
    }

    [Fact]
    public void NodeSolidTotals_AreUnknownWhenOnlyCuttingNodesAreMeasured()
    {
        var geometry = TakenAndUntakenBoxes(3, 4);
        geometry.NodeMeasures = [new RvtIO.Parsing.MeshNodeMeasure(3, 100, -9), new RvtIO.Parsing.MeshNodeMeasure(4, 100, -9)];

        Assert.Null(BrepMeasures.NodeSolidTotalsCurrent(geometry));
    }

    [Fact]
    public void Shells_OfACuttingNodeAreNotMeasuredFromTheSource()
    {
        var meshNode = new RvtIO.Parsing.GeometryNode(5, 0,
            [new BrFace { Marker = 0, SurfaceKind = SurfaceKind.Plane, Loops = [] }], 1) { SourceMeasures = (999, 888) };
        GeometryData Boxed()
        {
            var geometry = Box(2, 3, 4);
            foreach (var face in geometry.Faces) face.GeometryNode = 5;
            geometry.MeshNodes = [meshNode];
            return geometry;
        }

        var taken = SoleShell(Boxed());
        var geometry = Boxed();
        geometry.NodeMeasures = [new RvtIO.Parsing.MeshNodeMeasure(5, 52, -24)];
        var cutting = SoleShell(geometry);

        Assert.True(taken.SourceMeasured);
        Assert.Equal(999, taken.Area, 9);
        Assert.False(cutting.SourceMeasured);
        Assert.Equal(52, cutting.Area, 9);
    }

    [Fact]
    public void Pass_Column_LeavesOutTheExtrusionOfACuttingNode()
    {
        var (w, owner, placement) = NewWriter();
        int shape = AddTwoExtrusionBody(w, 0.5, 0.25, 1.5, 4.0);
        int column = AddProduct(w, "IFCCOLUMN", "GZGD7y6S5209$mGLi_sPlj", owner, placement, shape);
        var geometry = Box(1, 2, 5);
        // the last extrusion (0.5 x 0.25 x 4.0 = 0.5 m3) is the body of a node whose faces point into it
        geometry.NodeMeasures = [new RvtIO.Parsing.MeshNodeMeasure(2, 10, -0.5 / CubicFeet)];
        var element = new Element { Id = 22, CategoryId = -2001330, Geometry = geometry };
        var exporter = new IfcExporter();
        int lastSolid = Enumerable.Range(0, w.EntityCount).Select(w.EntityAt).Last(e => e.Type == "IFCEXTRUDEDAREASOLID").Id;
        exporter._bodyItemOrigins[lastSolid] = (geometry, 2);

        Apply(w, owner, new() { [22] = element }, new() { [column] = 22 }, exporter: exporter);

        Assert.Equal(1.5, Quantities(w, "IFCQUANTITYLENGTH").Single(q => q.Name == "Length").Value, 9);
        Assert.Equal(0.5 * 0.25, Quantities(w, "IFCQUANTITYAREA").Single(q => q.Name == "CrossSectionArea").Value, 9);
    }

    [Fact]
    public void Pass_Column_KeepsAnExtrusionThatOnlyHasTheVolumeOfACuttingNode()
    {
        var (w, owner, placement) = NewWriter();
        int shape = AddTwoExtrusionBody(w, 0.5, 0.25, 1.5, 4.0);
        int column = AddProduct(w, "IFCCOLUMN", "GZGD7y6S5209$mGLi_sPlj", owner, placement, shape);
        var geometry = Box(1, 2, 5);
        // a cutting node of 0.5 m3 exists, like the last extrusion (0.5 x 0.25 x 4.0), but no body item was written from it
        geometry.NodeMeasures = [new RvtIO.Parsing.MeshNodeMeasure(2, 10, -0.5 / CubicFeet)];
        var element = new Element { Id = 23, CategoryId = -2001330, Geometry = geometry };

        Apply(w, owner, new() { [23] = element }, new() { [column] = 23 });

        Assert.Equal(4.0, Quantities(w, "IFCQUANTITYLENGTH").Single(q => q.Name == "Length").Value, 9);
    }

    private static List<FaceArea.Coedge> UnitSquare() => Enumerable.Range(0, 4).Select(i =>
    {
        (double, double)[] corners = [(0, 0), (1, 0), (1, 1), (0, 1)];
        return new FaceArea.Coedge([corners[i], corners[(i + 1) % 4]], 6, false);
    }).ToList();

    [Theory]
    [InlineData(1, 1.0)]
    [InlineData(0, -1.0)]
    public void CubeFaces_HaveAPositiveSignedVolumeOnlyWhenTheyPointOutwards(int outwardOrient, double expectedVolume)
    {
        // (origin, X, Y, Orient): the six faces of the unit cube; a face points along X x Y when Orient is 1
        (double[] O, double[] X, double[] Y, int Orient)[] faces =
        [
            ([0, 0, 1], [1, 0, 0], [0, 1, 0], 1), ([0, 0, 0], [1, 0, 0], [0, 1, 0], 0),
            ([1, 0, 0], [0, 1, 0], [0, 0, 1], 1), ([0, 0, 0], [0, 1, 0], [0, 0, 1], 0),
            ([0, 1, 0], [0, 0, 1], [1, 0, 0], 1), ([0, 0, 0], [0, 0, 1], [1, 0, 0], 0),
        ];
        double area = 0, volume = 0;
        foreach (var (origin, x, y, orient) in faces)
        {
            var surface = new GRepParserV2.SurfaceRec
            {
                Kind = GRepParserV2.SurfKind.Plane, O = origin, X = x, Y = y,
                Z = [x[1] * y[2] - x[2] * y[1], x[2] * y[0] - x[0] * y[2], x[0] * y[1] - x[1] * y[0]],
                Orient = (byte)(orient == 1 ? outwardOrient : 1 - outwardOrient),
            };
            var measure = GRepParserV2.MeasureAnalyticFace(surface, [0.5, 0.5, 0.5], UnitSquare())!.Value;
            area += measure.Area;
            volume += measure.Volume;
        }
        Assert.Equal(6, area, 12);
        Assert.Equal(expectedVolume, volume, 12);
    }

    /// <summary>An extruded rectangle (x by y, depth) whose profile X axis is <paramref name="profileX"/> and whose solid has the
    /// frame <paramref name="axis"/> (extrusion direction) and <paramref name="refDirection"/>.</summary>
    private static int AddFramedExtrusionBody(StepWriter w, double x, double y, double depth, string axis, string refDirection, string profileX = "(1.,0.)")
    {
        int origin = w.Add("IFCCARTESIANPOINT", "(0.,0.)");
        int profileDirection = w.Add("IFCDIRECTION", profileX);
        int profilePlacement = w.Add("IFCAXIS2PLACEMENT2D", $"#{origin},#{profileDirection}");
        int profile = w.Add("IFCRECTANGLEPROFILEDEF", $".AREA.,$,#{profilePlacement},{StepWriter.Real(x)},{StepWriter.Real(y)}");
        int point3 = w.Add("IFCCARTESIANPOINT", "(0.,0.,0.)");
        int position = w.Add("IFCAXIS2PLACEMENT3D", $"#{point3},#{w.Add("IFCDIRECTION", axis)},#{w.Add("IFCDIRECTION", refDirection)}");
        int direction = w.Add("IFCDIRECTION", "(0.,0.,1.)");
        int solid = w.Add("IFCEXTRUDEDAREASOLID", $"#{profile},#{position},#{direction},{StepWriter.Real(depth)}");
        int context = w.Add("IFCGEOMETRICREPRESENTATIONCONTEXT", $"$,'Model',3,1.E-05,#{position},$");
        int representation = w.Add("IFCSHAPEREPRESENTATION", $"#{context},'Body','SweptSolid',(#{solid})");
        return w.Add("IFCPRODUCTDEFINITIONSHAPE", $"$,$,(#{representation})");
    }

    private static double SlabWidth(StepWriter w) => Quantities(w, "IFCQUANTITYLENGTH").Single(q => q.Name == "Width").Value;

    [Fact]
    public void Pass_FoundationLyingOnItsSide_WidthIsTheFirstSideOfItsProfileNotTheVerticalOne()
    {
        var (w, owner, placement) = NewWriter();
        // A pile cap 0.4 x 0.9 x 0.4: profile 0.4 by 0.9 extruded along the world X, the profile X axis along the world Y, so the
        // profile Y axis (0.9) is the vertical one. The world X extent rule of a floor would give 0.9.
        int shape = AddFramedExtrusionBody(w, 0.4, 0.9, 0.4, axis: "(1.,0.,0.)", refDirection: "(0.,1.,0.)");
        int slab = AddProduct(w, "IFCSLAB", "AZGD7y6S5209$mGLi_sPlj", owner, placement, shape);
        var cap = new Element { Id = 61, CategoryId = -2001300, Geometry = Box(0.4, 0.9, 0.4) };

        Apply(w, owner, new() { [61] = cap }, new() { [slab] = 61 });

        Assert.Equal(0.4, SlabWidth(w), 9);
    }

    [Fact]
    public void Pass_BaseSlabOfTheFloorCategory_RectangleTurnedAboutZ_WidthIsTheFirstSideOfItsProfile()
    {
        var (w, owner, placement) = NewWriter();
        int shape = AddFramedExtrusionBody(w, 6, 3, 0.5, axis: "(0.,0.,1.)", refDirection: "(1.,0.,0.)", profileX: "(0.,1.)");
        int slab = w.Add("IFCSLAB", $"'HZGD7y6S5209$mGLi_sPlj',#{owner},'P',$,$,#{placement},#{shape},'1',.BASESLAB.");

        Apply(w, owner, new() { [68] = new Element { Id = 68, CategoryId = -2000032 } }, new() { [slab] = 68 });

        // a floor would give the world X extent, 3; the reference gives the first side of the profile of a base slab
        Assert.Equal(6, SlabWidth(w), 9);
    }

    [Fact]
    public void Pass_Floor_RectangleTurnedAboutZ_WidthIsTheWorldXExtentOfItsProfile()
    {
        var (w, owner, placement) = NewWriter();
        // profile 6 by 3 whose X axis points along the world Y: the world X extent is the profile Y extent, 3
        int shape = AddFramedExtrusionBody(w, 6, 3, 0.5, axis: "(0.,0.,1.)", refDirection: "(1.,0.,0.)", profileX: "(0.,1.)");
        int slab = AddProduct(w, "IFCSLAB", "BZGD7y6S5209$mGLi_sPlj", owner, placement, shape);

        Apply(w, owner, new() { [62] = new Element { Id = 62, CategoryId = -2000032 } }, new() { [slab] = 62 });

        Assert.Equal(3, SlabWidth(w), 9);
    }

    [Fact]
    public void Pass_Floor_WithAVerticalPrism_WidthIsTheExtentOfTheFirstLoopOfItsLowerEndFaceNotTheOutlineOfTheBody()
    {
        var (w, owner, placement) = NewWriter();
        int shape = AddExtrusionBody(w, 6, 3, 0.5);
        int slab = AddProduct(w, "IFCSLAB", "CZGD7y6S5209$mGLi_sPlj", owner, placement, shape);
        var geometry = PrismOver(Rectangle(6, 3), 0.5);
        double[][] Hole(double z) => [[2, 0.5, z], [3, 0.5, z], [3, 2.5, z], [2, 2.5, z]];
        foreach (var (face, z) in new[] { (geometry.Faces[0], 0.0), (geometry.Faces[1], 0.5) })
            face.InnerVertices = [Hole(z).ToList()];
        geometry.Faces[0].FirstSourceLoop = Hole(0);
        geometry.Faces[1].FirstSourceLoop = Rectangle(6, 3).Select(p => new[] { p.X, p.Y, 0.5 }).ToList();

        Apply(w, owner, new() { [63] = new Element { Id = 63, CategoryId = -2000032, Geometry = geometry } }, new() { [slab] = 63 });

        // the hole is 1 wide; the outline of the body is 6 wide
        Assert.Equal(1 * 0.3048, SlabWidth(w), 9);
    }

    [Fact]
    public void Pass_Roof_WidthIsTheLargerSideOfTheBody()
    {
        var (w, owner, placement) = NewWriter();
        int shape = AddExtrusionBody(w, 6, 9, 0.5);
        int slab = w.Add("IFCSLAB", $"'DZGD7y6S5209$mGLi_sPlj',#{owner},'P',$,$,#{placement},#{shape},'1',.ROOF.");

        Apply(w, owner, new() { [64] = new Element { Id = 64, CategoryId = -2000035, Geometry = PrismOver(Rectangle(6, 9), 0.5) } }, new() { [slab] = 64 });

        Assert.Equal(9, SlabWidth(w), 9);
    }

    /// <summary>A pile: open shells without measures, the end caps plane faces of one node with an exact area each, and four side faces.</summary>
    private static GeometryData PileOfOpenShells(double length, double topCapArea, double topNormalZ)
    {
        var bottom = Face([0, 0, 0], [0, 1, 0], [1, 1, 0], [1, 0, 0]);
        bottom.PlaneSurface = true; bottom.GeometryNode = 6; bottom.QuadratureArea = 3.14;
        var top = Face([0, 0, length], [1, 0, length], [1, 1, length], [0, 1, length]);
        top.PlaneSurface = true; top.GeometryNode = 6; top.QuadratureArea = topCapArea;
        top.Normal = [0, 0, topNormalZ];
        var faces = new List<GeoFace> { bottom, top };
        double[][] ring = [[0, 0], [1, 0], [1, 1], [0, 1]];
        for (int i = 0; i < 4; i++)
        {
            var (a, b) = (ring[i], ring[(i + 1) % 4]);
            var side = Face([a[0], a[1], 0], [b[0], b[1], 0], [b[0], b[1], length], [a[0], a[1], length]);
            side.PlaneSurface = true; side.GeometryNode = 6;
            faces.Add(side);
        }
        return new GeometryData { Faces = faces, BrepSolids = [new GeoBrepSolid { IsClosed = false, FaceIndices = Enumerable.Range(0, 6).ToList() }] };
    }

    [Fact]
    public void Pass_FoundationPileOfOpenShells_NetVolumeIsTheCapAreaTimesTheDistanceOfTheOppositeCaps()
    {
        var (w, owner, placement) = NewWriter();
        int slab = AddProduct(w, "IFCSLAB", "EZGD7y6S5209$mGLi_sPlj", owner, placement);
        var pile = new Element { Id = 65, CategoryId = -2001300, Geometry = PileOfOpenShells(24.25, 3.14, 1) };

        Apply(w, owner, new() { [65] = pile }, new() { [slab] = 65 });

        AssertQuantities(Quantities(w, "IFCQUANTITYVOLUME"), ("NetVolume", 3.14 * 24.25 * CubicFeet));
    }

    [Fact]
    public void Pass_FoundationPileOfOpenShells_TessellatedSmallerTwinCapTakesTheLargerCapArea()
    {
        var (w, owner, placement) = NewWriter();
        int slab = AddProduct(w, "IFCSLAB", "GZGD7y6S5209$mGLi_sPlj", owner, placement);
        var pile = new Element { Id = 67, CategoryId = -2001300, Geometry = PileOfOpenShells(23.5, 3.12, 1) };

        Apply(w, owner, new() { [67] = pile }, new() { [slab] = 67 });

        AssertQuantities(Quantities(w, "IFCQUANTITYVOLUME"), ("NetVolume", 3.14 * 23.5 * CubicFeet));
    }

    [Theory]
    [InlineData(3.5, 1)]     // the caps have not the same area
    [InlineData(3.14, -1)]   // the caps face the same way
    public void Pass_FoundationPileOfOpenShells_CapsThatAreNoPairGiveNoVolume(double topCapArea, double topNormalZ)
    {
        var (w, owner, placement) = NewWriter();
        int slab = AddProduct(w, "IFCSLAB", "FZGD7y6S5209$mGLi_sPlj", owner, placement);
        var pile = new Element { Id = 66, CategoryId = -2001300, Geometry = PileOfOpenShells(24.25, topCapArea, topNormalZ) };

        Apply(w, owner, new() { [66] = pile }, new() { [slab] = 66 });

        Assert.Empty(Quantities(w, "IFCQUANTITYVOLUME"));
    }

    /// <summary>The faces of one open prismatic solid of a node (a square of side 2 in plan, standing on z = 0, moved by
    /// <paramref name="dx"/>): the two end caps with an exact area each, and the four side faces.</summary>
    private static List<GeoFace> OpenPrismFaces(int node, double dx, double length, double topCapArea = 4, bool withSides = true)
    {
        double[] P(double x, double y, double z) => [x + dx, y, z];
        var faces = new List<GeoFace>
        {
            Face(P(0, 0, 0), P(0, 2, 0), P(2, 2, 0), P(2, 0, 0)),
            Face(P(0, 0, length), P(2, 0, length), P(2, 2, length), P(0, 2, length)),
        };
        faces[0].QuadratureArea = 4;
        faces[1].QuadratureArea = topCapArea;
        if (withSides)
            for (int i = 0; i < 4; i++)
            {
                double[][] ring = [[0, 0], [2, 0], [2, 2], [0, 2]];
                var (a, b) = (ring[i], ring[(i + 1) % 4]);
                faces.Add(Face(P(a[0], a[1], 0), P(b[0], b[1], 0), P(b[0], b[1], length), P(a[0], a[1], length)));
            }
        foreach (var face in faces) { face.PlaneSurface = true; face.GeometryNode = node; }
        return faces;
    }

    /// <summary>Every group of faces is one open shell without measures.</summary>
    private static GeometryData OpenShells(params List<GeoFace>[] groups)
    {
        var faces = groups.SelectMany(group => group).ToList();
        int start = 0;
        var solids = new List<GeoBrepSolid>();
        foreach (var group in groups)
        {
            solids.Add(new GeoBrepSolid { IsClosed = false, FaceIndices = Enumerable.Range(start, group.Count).ToList() });
            start += group.Count;
        }
        return new GeometryData { Faces = faces, BrepSolids = solids };
    }

    private static List<(string Name, double Value)> FoundationVolume(GeometryData geometry)
    {
        var (w, owner, placement) = NewWriter();
        int slab = AddProduct(w, "IFCSLAB", "HZGD7y6S5209$mGLi_sPlj", owner, placement);
        Apply(w, owner, new() { [70] = new Element { Id = 70, CategoryId = -2001300, Geometry = geometry } }, new() { [slab] = 70 });
        return Quantities(w, "IFCQUANTITYVOLUME");
    }

    [Fact]
    public void Pass_FoundationOfOpenShells_StraightPrismWithSideFaces_NetVolumeIsTheCapAreaTimesTheLength()
    {
        AssertQuantities(FoundationVolume(OpenShells(OpenPrismFaces(1, 0, 10))), ("NetVolume", 40 * CubicFeet));
    }

    [Fact]
    public void Pass_FoundationOfOpenShells_BodyNarrowedBetweenEqualCaps_GivesNoVolume()
    {
        // two square frusta meet at z = 5: the caps have a side of 2, the middle a side of 1 (the true volume is 70/3, not 40)
        var faces = OpenPrismFaces(1, 0, 10, withSides: false);
        for (int half = 0; half < 2; half++)
        {
            double za = half * 5, zb = za + 5, a = half == 0 ? 1 : 0.5, b = half == 0 ? 0.5 : 1;
            double[][] lo = [[1 - a, 1 - a, za], [1 + a, 1 - a, za], [1 + a, 1 + a, za], [1 - a, 1 + a, za]];
            double[][] hi = [[1 - b, 1 - b, zb], [1 + b, 1 - b, zb], [1 + b, 1 + b, zb], [1 - b, 1 + b, zb]];
            for (int i = 0; i < 4; i++)
                faces.Add(new GeoFace { GeometryNode = 1, PlaneSurface = true, OuterVertices = [lo[i], lo[(i + 1) % 4], hi[(i + 1) % 4], hi[i]] });
        }

        Assert.Empty(FoundationVolume(OpenShells(faces)));
    }

    [Fact]
    public void Pass_FoundationOfOpenShells_SideFaceLeaningAwayFromTheCapNormal_GivesNoVolume()
    {
        var faces = OpenPrismFaces(1, 0, 10);
        // the top of the first side moved out: its vertices stay on the two cap planes, the face is not parallel to the normal
        faces[2].OuterVertices![2][1] -= 1;
        faces[2].OuterVertices![3][1] -= 1;

        Assert.Empty(FoundationVolume(OpenShells(faces)));
    }

    [Fact]
    public void Pass_FoundationOfOpenShells_RoundPileOfFacetedSideFaces_KeepsTheVolume()
    {
        const int facets = 16;
        const double length = 24.25, area = 3.14;
        double[] Ring(int i, double z, int steps = facets) => [Math.Cos(2 * Math.PI * i / steps), Math.Sin(2 * Math.PI * i / steps), z];
        // as in the sample: the caps are polygons of twice as many points as the side faces have facets
        var faces = new List<GeoFace>
        {
            Face(Enumerable.Range(0, 2 * facets).Reverse().Select(i => Ring(i, 0, 2 * facets)).ToArray()),
            Face(Enumerable.Range(0, 2 * facets).Select(i => Ring(i, length, 2 * facets)).ToArray()),
        };
        for (int i = 0; i < facets; i++)
        {
            var side = Face(Ring(i, 0), Ring(i + 1, 0), Ring(i + 1, length), Ring(i, length));
            side.CurvedSurface = true;
            faces.Add(side);
        }
        faces[0].QuadratureArea = faces[1].QuadratureArea = area;
        foreach (var face in faces) { face.GeometryNode = 6; face.PlaneSurface = !face.CurvedSurface; }

        AssertQuantities(FoundationVolume(OpenShells(faces)), ("NetVolume", area * length * CubicFeet));
    }

    [Fact]
    public void Pass_FoundationOfOpenShells_CylinderFaceWithAxisAlongTheCapNormal_KeepsTheVolume()
    {
        var faces = OpenPrismFaces(1, 0, 10);
        // a curved face whose boundary runs along both cap planes but has no usable loop normal (a bow tie): its analytic axis says it is straight
        faces.Add(new GeoFace
        {
            GeometryNode = 1, CurvedSurface = true, AnalyticCylinderAxis = [0, 0, 1], AnalyticCylinderRadius = 1,
            OuterVertices = [[0, 0, 0], [2, 0, 10], [2, 0, 0], [0, 0, 10]],
        });

        AssertQuantities(FoundationVolume(OpenShells(faces)), ("NetVolume", 40 * CubicFeet));
    }

    [Fact]
    public void Pass_FoundationOfOpenShells_CylinderFaceWithAxisAcrossTheCapNormal_GivesNoVolume()
    {
        var faces = OpenPrismFaces(1, 0, 10);
        faces[2].PlaneSurface = false; faces[2].CurvedSurface = true; faces[2].AnalyticCylinderAxis = [1, 0, 0];

        Assert.Empty(FoundationVolume(OpenShells(faces)));
    }

    [Fact]
    public void Pass_FoundationOfOpenShells_TwistedPrismOfEqualCaps_GivesNoVolume()
    {
        // the top square is the bottom one turned by 90 degrees; the side faces are bent quads whose loop normal is perpendicular to the cap
        // normal, but whose corners are not on a plane (the true volume is 80/3, not 40)
        var faces = OpenPrismFaces(1, 0, 10, withSides: false);
        double[][] lo = [[0, 0, 0], [2, 0, 0], [2, 2, 0], [0, 2, 0]];
        double[][] hi = [[2, 0, 10], [2, 2, 10], [0, 2, 10], [0, 0, 10]];
        for (int i = 0; i < 4; i++)
            faces.Add(new GeoFace { GeometryNode = 1, CurvedSurface = true, OuterVertices = [lo[i], lo[(i + 1) % 4], hi[(i + 1) % 4], hi[i]] });
        faces[1].OuterVertices = hi.ToList();

        Assert.Empty(FoundationVolume(OpenShells(faces)));
    }

    [Fact]
    public void Pass_FoundationOfOpenShells_CapsWithoutSideFaces_GiveNoVolume()
    {
        Assert.Empty(FoundationVolume(OpenShells(OpenPrismFaces(1, 0, 10, withSides: false))));
    }

    [Fact]
    public void Pass_FoundationOfOpenShells_SideFacesThatCoverOnlyPartOfTheCapBoundary_KeepTheVolume()
    {
        // as for the round piles of the sample: the other part of the lateral surface lies in another node
        var faces = OpenPrismFaces(1, 0, 10);
        faces.RemoveRange(3, 3);

        AssertQuantities(FoundationVolume(OpenShells(faces)), ("NetVolume", 40 * CubicFeet));
    }

    [Fact]
    public void Pass_FoundationOfOpenShells_SquareCapAndRoundCapOfTheSameAreaWithOneSideFace_GiveNoVolume()
    {
        // the side face is a rectangle of the square, its top edge is not on the round cap: the body between the caps is a loft (more than 40)
        var faces = OpenPrismFaces(1, 0, 10, withSides: false);
        double r = Math.Sqrt(4 / Math.PI);
        faces[1].OuterVertices = Enumerable.Range(0, 32).Select(k => new[] { 1 + r * Math.Cos(2 * Math.PI * k / 32), 1 + r * Math.Sin(2 * Math.PI * k / 32), 10.0 }).ToList();
        faces.Add(new GeoFace { GeometryNode = 1, PlaneSurface = true, OuterVertices = [[0, 0, 0], [2, 0, 0], [2, 0, 10], [0, 0, 10]] });

        Assert.Empty(FoundationVolume(OpenShells(faces)));
    }

    [Fact]
    public void Pass_FoundationOfOpenShells_SideFaceWithAPointBetweenTheCapPlanes_GivesNoVolume()
    {
        var faces = OpenPrismFaces(1, 0, 10);
        // a vertical rectangle that stops half way: not a side from cap to cap
        faces.Add(new GeoFace { GeometryNode = 1, PlaneSurface = true, OuterVertices = [[0, 0, 0], [2, 0, 0], [2, 0, 5], [0, 0, 5]] });

        Assert.Empty(FoundationVolume(OpenShells(faces)));
    }

    [Fact]
    public void Pass_FoundationOfOpenShells_PrismWithoutAGeometryNode_KeepsTheVolume()
    {
        AssertQuantities(FoundationVolume(OpenShells(OpenPrismFaces(0, 0, 10))), ("NetVolume", 40 * CubicFeet));
    }

    [Fact]
    public void Pass_FoundationOfOpenShells_NetAreaIsTheCapOfANodeThatCounts()
    {
        // the cap pair of the negative node comes first and is larger
        var excluded = OpenPrismFaces(-1, 10, 10);
        foreach (var face in excluded.Take(2)) face.QuadratureArea = 9;
        var geometry = OpenShells(excluded, OpenPrismFaces(2, 0, 10));
        var (w, owner, placement) = NewWriter();
        int slab = AddProduct(w, "IFCSLAB", "IZGD7y6S5209$mGLi_sPlj", owner, placement);
        Apply(w, owner, new() { [70] = new Element { Id = 70, CategoryId = -2001300, Geometry = geometry } }, new() { [slab] = 70 });

        AssertQuantities(Quantities(w, "IFCQUANTITYAREA"), ("GrossArea", 4 * SquareFeet), ("NetArea", 4 * SquareFeet));
    }

    private static List<(string Name, double Value)> FoundationAreas(GeometryData geometry, double? hostArea = null)
    {
        var (w, owner, placement) = NewWriter();
        int slab = AddProduct(w, "IFCSLAB", "IZGD7y6S5209$mGLi_sPlj", owner, placement);
        Apply(w, owner, new() { [70] = new Element { Id = 70, CategoryId = -2001300, Geometry = geometry, Parameters = hostArea is { } area
            ? [new Parameter { Id = -1012805, Value = area, StorageType = StorageType.Double, Definition = new Definition { Id = -1012805, Name = "HOST_AREA_COMPUTED" } }] : [] } },
            new() { [slab] = 70 });
        return Quantities(w, "IFCQUANTITYAREA");
    }

    /// <summary>An open prism whose caps carry an exact area of 9 (the area of a node that must not count) or none at all.</summary>
    private static List<GeoFace> PrismWithCapArea(int node, double dx, double? capArea)
    {
        var faces = OpenPrismFaces(node, dx, 10);
        faces[0].QuadratureArea = faces[1].QuadratureArea = capArea;
        return faces;
    }

    [Fact]
    public void Pass_FoundationOfOpenShells_NetAreaOfANodeWithoutACapArea_LeavesOutANodeOfANegativeMeasure()
    {
        var geometry = OpenShells(PrismWithCapArea(1, 10, 9), PrismWithCapArea(2, 0, null));
        geometry.NodeMeasures = [new RvtIO.Parsing.MeshNodeMeasure(1, 10, -40)];

        AssertQuantities(FoundationAreas(geometry), ("GrossArea", 4 * SquareFeet), ("NetArea", 4 * SquareFeet));
    }

    [Fact]
    public void Pass_FoundationOfOpenShells_NetAreaOfANodeWithoutACapArea_LeavesOutANegativeNode()
    {
        var geometry = OpenShells(PrismWithCapArea(-1, 10, 9), PrismWithCapArea(2, 0, null));

        AssertQuantities(FoundationAreas(geometry), ("GrossArea", 4 * SquareFeet), ("NetArea", 4 * SquareFeet));
    }

    [Fact]
    public void Pass_FoundationOfOpenShells_NetAreaWithoutAnyCapArea_LeavesOutANegativeNode()
    {
        var geometry = OpenShells(PrismWithCapArea(-1, 10, null), PrismWithCapArea(2, 0, null));

        AssertQuantities(FoundationAreas(geometry), ("GrossArea", 4 * SquareFeet), ("NetArea", 4 * SquareFeet));
    }

    private static GeometryData ClosedShells(params List<GeoFace>[] groups)
    {
        var geometry = OpenShells(groups);
        foreach (var solid in geometry.BrepSolids!) solid.IsClosed = true;
        return geometry;
    }

    [Fact]
    public void Pass_FoundationOfClosedShells_WithoutAnyCapArea_NetVolumeLeavesOutANegativeNode()
    {
        var geometry = ClosedShells(PrismWithCapArea(-1, 10, null), PrismWithCapArea(2, 0, null));

        AssertQuantities(FoundationVolume(geometry), ("NetVolume", 40 * CubicFeet));
    }

    [Fact]
    public void Pass_FoundationOfClosedShells_WithoutAnyCapArea_NetVolumeLeavesOutACuttingNode()
    {
        var geometry = ClosedShells(PrismWithCapArea(1, 10, null), PrismWithCapArea(2, 0, null));
        geometry.NodeMeasures = [new RvtIO.Parsing.MeshNodeMeasure(1, 10, -40)];

        AssertQuantities(FoundationVolume(geometry), ("NetVolume", 40 * CubicFeet));
    }

    [Fact]
    public void Pass_FoundationOfClosedShells_ExcludedNodeWithAZeroCapAreaFirst_NetVolumeLeavesItOut()
    {
        var geometry = ClosedShells(PrismWithCapArea(-1, 10, 0), PrismWithCapArea(2, 0, 4));

        AssertQuantities(FoundationVolume(geometry), ("NetVolume", 40 * CubicFeet));
    }

    [Fact]
    public void Pass_FoundationOfOpenShells_OnlyNodesThatDoNotCount_HaveNoNetArea()
    {
        var geometry = OpenShells(PrismWithCapArea(-1, 10, 9));

        Assert.Empty(FoundationAreas(geometry));
    }

    [Fact]
    public void Pass_FoundationOfOpenShells_OnlyNodesThatDoNotCount_NetAreaFallsBackToTheComputedHostArea()
    {
        var geometry = OpenShells(PrismWithCapArea(-1, 10, 9));

        AssertQuantities(FoundationAreas(geometry, hostArea: 3), ("GrossArea", 3 * SquareFeet), ("NetArea", 3 * SquareFeet));
    }

    [Fact]
    public void Pass_FoundationOfOpenShells_WithoutAnExcludedNode_NetAreaIsTheTopSurfaceOfEveryShell()
    {
        var geometry = OpenShells(PrismWithCapArea(1, 10, null), PrismWithCapArea(2, 0, null));

        AssertQuantities(FoundationAreas(geometry), ("GrossArea", 8 * SquareFeet), ("NetArea", 8 * SquareFeet));
    }

    [Fact]
    public void Pass_FoundationOfOpenShells_TwinCapOnePointFivePercentSmaller_KeepsTheVolumeOfTheLargerCap()
    {
        // the twin is the tessellation of the cap: its points are a little inside the side faces
        var faces = OpenPrismFaces(1, 0, 10, topCapArea: 4 * 0.985);
        double shrink = Math.Sqrt(0.985);
        faces[1].OuterVertices = faces[1].OuterVertices!.Select(p => new[] { 1 + (p[0] - 1) * shrink, 1 + (p[1] - 1) * shrink, p[2] }).ToList();

        AssertQuantities(FoundationVolume(OpenShells(faces)), ("NetVolume", 40 * CubicFeet));
    }

    [Fact]
    public void Pass_FoundationOfOpenShells_CapsWithPlanesThatAreNotParallel_GiveNoVolume()
    {
        var faces = OpenPrismFaces(1, 0, 10);
        faces[1].Normal = [0, Math.Sin(0.1), -Math.Cos(0.1)];

        Assert.Empty(FoundationVolume(OpenShells(faces)));
    }

    [Fact]
    public void Pass_FoundationOfOpenShells_NodeWithASingleCap_GivesNoVolume()
    {
        var faces = OpenPrismFaces(1, 0, 10);
        faces[1].QuadratureArea = null;

        Assert.Empty(FoundationVolume(OpenShells(faces)));
    }

    [Fact]
    public void Pass_FoundationOfOpenShells_NegativeNodeGivesNoVolume()
    {
        Assert.Empty(FoundationVolume(OpenShells(OpenPrismFaces(-1, 0, 10))));
    }

    [Fact]
    public void Pass_FoundationOfOpenShells_NegativeNodeNextToAValidOne_IsLeftOut()
    {
        var geometry = OpenShells(OpenPrismFaces(-1, 10, 10), OpenPrismFaces(2, 0, 5));

        AssertQuantities(FoundationVolume(geometry), ("NetVolume", 20 * CubicFeet));
    }

    [Fact]
    public void Pass_FoundationOfOpenShells_NodeOfANegativeMeasure_IsLeftOut()
    {
        var geometry = OpenShells(OpenPrismFaces(1, 10, 10), OpenPrismFaces(2, 0, 5));
        geometry.NodeMeasures = [new RvtIO.Parsing.MeshNodeMeasure(1, 10, -40)];

        AssertQuantities(FoundationVolume(geometry), ("NetVolume", 20 * CubicFeet));
    }

    [Fact]
    public void Pass_FoundationOfOpenShells_TwoPrismaticNodes_VolumesAreAdded()
    {
        var geometry = OpenShells(OpenPrismFaces(1, 0, 10), OpenPrismFaces(2, 10, 10));

        AssertQuantities(FoundationVolume(geometry), ("NetVolume", 80 * CubicFeet));
    }

    [Fact]
    public void Pass_FoundationOfOpenShells_MeasuredNodeAndOpenPrismaticNode_AreAddedWithoutCountingTheMeasuredOneTwice()
    {
        var geometry = OpenShells(OpenPrismFaces(1, 0, 10), OpenPrismFaces(2, 10, 5));
        geometry.NodeMeasures = [new RvtIO.Parsing.MeshNodeMeasure(1, 10, 30)];

        // node 1: its measure, 30 (its caps would give 40); node 2: its caps, 20
        AssertQuantities(FoundationVolume(geometry), ("NetVolume", 50 * CubicFeet));
    }

    [Fact]
    public void Pass_FoundationOfOpenShells_ClosedShellAndOpenPrismaticNode_AreAdded()
    {
        var closed = Box(2, 2, 3);
        foreach (var face in closed.Faces) face.GeometryNode = 1;
        var open = OpenPrismFaces(2, 10, 10);
        var geometry = new GeometryData
        {
            Faces = closed.Faces.Concat(open).ToList(),
            BrepSolids =
            [
                new GeoBrepSolid { IsClosed = true, FaceIndices = Enumerable.Range(0, 6).ToList() },
                new GeoBrepSolid { IsClosed = false, FaceIndices = Enumerable.Range(6, open.Count).ToList() },
            ],
        };

        AssertQuantities(FoundationVolume(geometry), ("NetVolume", (12 + 40) * CubicFeet));
    }

    [Fact]
    public void Pass_FoundationOfOpenShells_ClosedShellThatIsAlsoAStraightPrism_IsCountedOnce()
    {
        // a closed box turned so that no face is horizontal (it has no vertical extrusion): its closed shell and its caps both give 40
        var faces = OpenPrismFaces(1, 0, 10);
        double[] e1 = [1 / Math.Sqrt(2), -1 / Math.Sqrt(2), 0], e3 = [1 / Math.Sqrt(3), 1 / Math.Sqrt(3), 1 / Math.Sqrt(3)];
        double[] e2 = [e3[1] * e1[2] - e3[2] * e1[1], e3[2] * e1[0] - e3[0] * e1[2], e3[0] * e1[1] - e3[1] * e1[0]];
        foreach (var face in faces)
        {
            double[] Turn(double[] p) => new[] { 0, 1, 2 }.Select(k => p[0] * e1[k] + p[1] * e2[k] + p[2] * e3[k]).ToArray();
            face.OuterVertices = face.OuterVertices!.Select(Turn).ToList();
            face.Origin = Turn(face.Origin!);
            face.Normal = Turn(face.Normal!);
        }
        var geometry = new GeometryData
        {
            Faces = faces,
            BrepSolids = [new GeoBrepSolid { IsClosed = true, FaceIndices = Enumerable.Range(0, faces.Count).ToList() }],
        };

        AssertQuantities(FoundationVolume(geometry), ("NetVolume", 40 * CubicFeet));
    }

    // ---- round columns: exact end faces beside a tessellated side ----

    /// <summary>A vertical solid on a regular polygon of <paramref name="sides"/> corners of radius 0.5 and height 10 feet as the decoded
    /// geometry holds a round one: the node with the integrals lists its two exact end faces (the top one smaller by
    /// <paramref name="topShortfall"/>, shifted along x by <paramref name="topShift"/>, raised by <paramref name="topTilt"/> times x), the
    /// tessellated node without integrals lists the side facets and a copy of the bottom end; the shell of the instance is that open
    /// tessellated node. The exact area of an end face is that of the circle, or of the polygon with <paramref name="polygonArea"/>.
    /// <paramref name="stray"/> pushes one corner of a side facet off the cylinder, <paramref name="extraSolid"/> adds a second solid node
    /// without integrals, <paramref name="hole"/> gives the bottom end a hole, <paramref name="splitCapNodes"/> puts the top end in a node of
    /// its own that measures the whole solid as well. <paramref name="offset"/> moves every vertex (x, y, z) and <paramref name="float32"/>
    /// rounds every coordinate to a 32-bit number, as mesh vertices are read.</summary>
    internal static GeometryData RoundColumnGeometry(double topShortfall = 0, double topShift = 0, double topTilt = 0, double height = 10,
        double stray = 0, bool extraSolid = false, int sides = 12, bool polygonArea = false, bool hole = false, bool splitCapNodes = false,
        double[]? offset = null, bool float32 = false, bool planarSides = false)
    {
        const double radius = 0.5;
        double[] Corner(int k, double z) => [radius * Math.Cos(2 * Math.PI * k / sides), radius * Math.Sin(2 * Math.PI * k / sides), z];
        var bottom = Enumerable.Range(0, sides).Select(k => Corner(k, 0)).ToList();
        var top = Enumerable.Range(0, sides).Select(k => Corner(k, height)).Select(p => new[] { p[0] + topShift, p[1], p[2] + topTilt * p[0] }).ToList();
        double circle = Math.PI * radius * radius;
        double capArea = polygonArea ? sides / 2.0 * radius * radius * Math.Sin(2 * Math.PI / sides) : circle;
        var faces = new List<GeoFace>
        {
            new() { GeometryNode = 10, PlaneSurface = true, QuadratureArea = capArea, OuterVertices = bottom, Origin = bottom[0], Normal = [0, 0, -1] },
            new() { GeometryNode = splitCapNodes ? 11 : 10, PlaneSurface = true, QuadratureArea = capArea - topShortfall, OuterVertices = top, Origin = top[0], Normal = [-topTilt, 0, 1] },
            new() { GeometryNode = 18, PlaneSurface = true, OuterVertices = bottom, Origin = bottom[0], Normal = [0, 0, -1] },
        };
        if (hole) faces[0].InnerVertices = [Enumerable.Range(0, 8).Select(k => new[] { 0.1 * Math.Cos(2 * Math.PI * k / 8), 0.1 * Math.Sin(2 * Math.PI * k / 8), 0.0 }).ToList()];
        for (int k = 0; k < sides; k++)
            faces.Add(new() { GeometryNode = 18, PlaneSurface = planarSides, OuterVertices = [Corner(k, 0), Corner(k + 1, 0), Corner(k + 1, height), Corner(k, height)] });
        if (stray > 0) faces[3].OuterVertices![2] = [(radius + stray) * Math.Cos(2 * Math.PI / sides), (radius + stray) * Math.Sin(2 * Math.PI / sides), height];
        var solids = new List<GeoBrepSolid> { new() { IsClosed = false, FaceIndices = [.. Enumerable.Range(2, sides + 1)] } };
        if (extraSolid)
        {
            // a closed 1 x 1 x 1 foot box beside the cylinder: no integrals, so only the shells of the instance measure it
            double[][] p = [[3, 0, 0], [4, 0, 0], [4, 1, 0], [3, 1, 0], [3, 0, 1], [4, 0, 1], [4, 1, 1], [3, 1, 1]];
            int[][] box = [[0, 3, 2, 1], [4, 5, 6, 7], [0, 1, 5, 4], [1, 2, 6, 5], [2, 3, 7, 6], [3, 0, 4, 7]];
            int first = faces.Count;
            foreach (var corners in box)
                faces.Add(new() { GeometryNode = 30, OuterVertices = corners.Select(i => p[i]).ToList() });
            solids.Add(new GeoBrepSolid { IsClosed = true, FaceIndices = [.. Enumerable.Range(first, box.Length)] });
        }
        if (offset != null || float32)
            foreach (var face in faces)
            {
                double[] Move(double[] p) => p.Select((v, axis) => float32 ? (double)(float)(v + (offset?[axis] ?? 0)) : v + (offset?[axis] ?? 0)).ToArray();
                face.OuterVertices = face.OuterVertices!.Select(Move).ToList();
                face.Origin = Move(face.Origin!);
            }
        var measures = new List<RvtIO.Parsing.MeshNodeMeasure> { new(10, 2 * Math.PI * radius * height + 2 * circle, circle * height) };
        if (splitCapNodes) measures.Add(new(11, 2 * Math.PI * radius * height + 2 * circle, circle * height));
        return new GeometryData { Faces = faces, BrepSolids = solids, NodeMeasures = measures };
    }

    private static StepWriter ColumnWriter(GeometryData geometry)
    {
        var (w, owner, placement) = NewWriter();
        int column = AddProduct(w, "IFCCOLUMN", "RZGD7y6S5209$mGLi_sPlj", owner, placement);
        Apply(w, owner, new() { [15] = new Element { Id = 15, CategoryId = -2001330, Geometry = geometry } }, new() { [column] = 15 });
        return w;
    }

    private static List<(string Name, double Value)> ColumnQuantities(GeometryData geometry)
    {
        var w = ColumnWriter(geometry);
        return new[] { "IFCQUANTITYLENGTH", "IFCQUANTITYAREA", "IFCQUANTITYVOLUME" }.SelectMany(type => Quantities(w, type)).ToList();
    }

    /// <summary>The column quantities of a geometry equal those of the same geometry without the measures of its nodes (the base: only the shells).</summary>
    private static void AssertColumnKeepsItsQuantitiesWithoutNodeMeasures(Func<GeometryData> geometry)
    {
        var measured = ColumnQuantities(geometry());
        var withoutMeasures = geometry();
        withoutMeasures.NodeMeasures = null;
        var baseline = ColumnQuantities(withoutMeasures);
        Assert.Equal(baseline.Select(q => q.Name), measured.Select(q => q.Name));
        for (int i = 0; i < baseline.Count; i++) Assert.Equal(baseline[i].Value, measured[i].Value, 12);
    }

    [Fact]
    public void Pass_Column_Round_TakesTheExtrusionDataOfItsCircleAndTheMeasuresOfTheNodeWithTheEndFaces()
    {
        double circle = Math.PI * 0.25;

        var w = ColumnWriter(RoundColumnGeometry());

        AssertQuantities(Quantities(w, "IFCQUANTITYLENGTH"), ("Length", 10 * 0.3048));
        AssertQuantities(Quantities(w, "IFCQUANTITYAREA"),
            ("CrossSectionArea", circle * SquareFeet),
            ("OuterSurfaceArea", 10 * 0.3048 * 1000 * Math.PI * 1.0 * 0.3048 * 1000));
        AssertQuantities(Quantities(w, "IFCQUANTITYVOLUME"), ("GrossVolume", 10 * 0.3048 * 1000 * circle * SquareFeet), ("NetVolume", circle * 10 * CubicFeet));
    }

    [Fact]
    public void Pass_Column_RoundWithEndFacesOfSlightlyDifferentArea_HasNoExtrusionDataAndTakesTheMeasuresOfTheNodeWithTheEndFaces()
    {
        double circle = Math.PI * 0.25;

        var w = ColumnWriter(RoundColumnGeometry(topShortfall: 0.003 * Math.PI * 0.25));

        Assert.Empty(Quantities(w, "IFCQUANTITYLENGTH"));
        // the cross section is half the surface of the solid, the volume the one of the node
        AssertQuantities(Quantities(w, "IFCQUANTITYAREA"), ("CrossSectionArea", (2 * Math.PI * 0.5 * 10 + 2 * circle) / 2 * SquareFeet));
        AssertQuantities(Quantities(w, "IFCQUANTITYVOLUME"), ("NetVolume", circle * 10 * CubicFeet));
    }

    public static TheoryData<string> NotARoundPrism => new()
    {
        "different circles", "end faces 6% apart in area", "corner off the cylinder", "tilted end face", "zero height", "hole in an end face",
        "4 corners", "5 corners", "6 corners", "7 corners", "8-gon", "12-gon", "16-gon", "40-gon with planar facets",
    };

    private static GeometryData NotARoundPrismGeometry(string kind) => kind switch
    {
        "different circles" => RoundColumnGeometry(topShift: 0.1),
        "end faces 6% apart in area" => RoundColumnGeometry(topShortfall: 0.05),
        "corner off the cylinder" => RoundColumnGeometry(stray: 0.05),
        "tilted end face" => RoundColumnGeometry(topTilt: 0.1),
        "zero height" => RoundColumnGeometry(height: 0),
        "hole in an end face" => RoundColumnGeometry(hole: true),
        // fewer than eight corners on the circle, the exact area that of the circle: the corner count refuses them
        "4 corners" => RoundColumnGeometry(sides: 4),
        "5 corners" => RoundColumnGeometry(sides: 5),
        "6 corners" => RoundColumnGeometry(sides: 6),
        "7 corners" => RoundColumnGeometry(sides: 7),
        // regular polygons with their own exact area: the area against the circle refuses them
        "8-gon" => RoundColumnGeometry(sides: 8, polygonArea: true),
        "12-gon" => RoundColumnGeometry(sides: 12, polygonArea: true),
        "16-gon" => RoundColumnGeometry(sides: 16, polygonArea: true),
        // the exact end area of a regular 40-gon is within the area tolerance of its circle: only the planar facets refuse it
        "40-gon with planar facets" => RoundColumnGeometry(sides: 40, polygonArea: true, planarSides: true),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    [Theory]
    [MemberData(nameof(NotARoundPrism))]
    public void Pass_Column_NotARoundPrism_HasNoExtrusionDataAndTheQuantitiesOfTheBase(string kind)
    {
        var w = ColumnWriter(NotARoundPrismGeometry(kind));

        Assert.Empty(Quantities(w, "IFCQUANTITYLENGTH"));
        Assert.DoesNotContain(Quantities(w, "IFCQUANTITYAREA"), q => q.Name == "OuterSurfaceArea");
        // the measures of the node stand for the solid of a round column only: the volume of the shells is the one of the base
        AssertColumnKeepsItsQuantitiesWithoutNodeMeasures(() => NotARoundPrismGeometry(kind));
    }

    [Fact]
    public void Pass_Column_MeasuredFlatNodeAndASecondSolidWithoutMeasure_KeepsTheVolumeOfEverySolid()
    {
        // the same open shell of the side without the box (its end faces on two different circles make it no cylinder): the box adds its own volume
        double withoutBox = ColumnQuantities(RoundColumnGeometry(topShift: 0.1)).Single(q => q.Name == "NetVolume").Value;

        var quantities = ColumnQuantities(RoundColumnGeometry(extraSolid: true));

        Assert.DoesNotContain(quantities, q => q.Name == "Length");
        Assert.Equal(withoutBox + 1 * CubicFeet, quantities.Single(q => q.Name == "NetVolume").Value, 9);
        AssertColumnKeepsItsQuantitiesWithoutNodeMeasures(() => RoundColumnGeometry(extraSolid: true));
    }

    [Fact]
    public void Pass_Column_RectangularWithExactFlatNodes_KeepsTheQuantitiesOfTheBase()
    {
        // a 1 x 2 x 10 foot box: its two exact end faces under the measured node, its four sides under a node without integrals
        static GeometryData Box()
        {
            double[][] p = [[0, 0, 0], [1, 0, 0], [1, 2, 0], [0, 2, 0], [0, 0, 10], [1, 0, 10], [1, 2, 10], [0, 2, 10]];
            int[][] corners = [[0, 3, 2, 1], [4, 5, 6, 7], [0, 1, 5, 4], [1, 2, 6, 5], [2, 3, 7, 6], [3, 0, 4, 7]];
            var faces = corners.Select((c, i) => new GeoFace
            {
                GeometryNode = i < 2 ? 10 : 18, PlaneSurface = true, QuadratureArea = i < 2 ? 2 : null,
                OuterVertices = c.Select(k => p[k]).ToList(), Origin = p[c[0]], Normal = i == 0 ? [0, 0, -1] : i == 1 ? [0, 0, 1] : [0, 0, 0],
            }).ToList();
            return new GeometryData
            {
                Faces = faces,
                BrepSolids = [new GeoBrepSolid { IsClosed = false, FaceIndices = [2, 3, 4, 5] }],
                NodeMeasures = [new RvtIO.Parsing.MeshNodeMeasure(10, 2 * 2 + 2 * 3 * 10, 2 * 10)],
            };
        }

        AssertColumnKeepsItsQuantitiesWithoutNodeMeasures(Box);
    }

    [Fact]
    public void Pass_Column_Round_FarFromTheOriginWithVerticesOf32BitNumbers_StaysACylinder()
    {
        var w = ColumnWriter(RoundColumnGeometry(offset: [75.3, -60.7, 50], float32: true));

        AssertQuantities(Quantities(w, "IFCQUANTITYLENGTH"), ("Length", 10 * 0.3048));
        // the radius is the one fitted to the rounded corners: the circle area agrees to a millionth, not to the last digit
        double gross = 10 * 0.3048 * 1000 * Math.PI * 0.25 * SquareFeet;
        Assert.InRange(Quantities(w, "IFCQUANTITYVOLUME").Single(q => q.Name == "GrossVolume").Value, gross * (1 - 1e-4), gross * (1 + 1e-4));
        Assert.Equal(Math.PI * 0.25 * 10 * CubicFeet, Quantities(w, "IFCQUANTITYVOLUME").Single(q => q.Name == "NetVolume").Value, 9);
    }

    [Fact]
    public void Pass_Column_Round_WithItsEndFacesInTwoMeasuredNodes_DoesNotCountTheSolidTwice()
    {
        var w = ColumnWriter(RoundColumnGeometry(splitCapNodes: true));

        // two nodes could each measure the whole solid: neither is taken, the volume is the one of the shells of the base
        Assert.Single(Quantities(w, "IFCQUANTITYLENGTH"));
        AssertColumnKeepsItsQuantitiesWithoutNodeMeasures(() => RoundColumnGeometry(splitCapNodes: true));
    }

    [Fact]
    public void Pass_Column_WithTheExtrusionOfItsBody_TakesTheVolumeOfTheNodeWithTheEndFacesBeforeTheOpenShell()
    {
        var (w, owner, placement) = NewWriter();
        int shape = AddFramedExtrusionBody(w, 1, 1, 2.0, axis: "(0.,0.,1.)", refDirection: "(1.,0.,0.)");
        int column = AddProduct(w, "IFCCOLUMN", "RZGD7y6S5209$mGLi_sPlj", owner, placement, shape);

        Apply(w, owner, new() { [15] = new Element { Id = 15, CategoryId = -2001330, Geometry = RoundColumnGeometry() } }, new() { [column] = 15 });

        // the extrusion data are the ones of the body; the volume of a cylinder comes from the measured node, not from the open shell of its side
        Assert.Equal(2.0, Quantities(w, "IFCQUANTITYLENGTH").Single(q => q.Name == "Length").Value, 9);
        Assert.Equal(Math.PI * 0.25 * 10 * CubicFeet, Quantities(w, "IFCQUANTITYVOLUME").Single(q => q.Name == "NetVolume").Value, 9);
    }
}
