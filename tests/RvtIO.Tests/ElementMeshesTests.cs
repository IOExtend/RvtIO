using System.Runtime.CompilerServices;
using RvtIO.Internal;
using RvtIO.Meshes;
using RvtIO.Model;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

/// <summary>The public triangle meshes (<see cref="ElementMeshes"/>) on synthetic geometry: triangulation, materials, hidden
/// nodes, the decision between a symbol placement and the element's own triangles, and winding.</summary>
public class ElementMeshesTests
{
    private const int Phase = 7;
    private const int SymbolId = 2;
    private const int InstanceId = 1;
    private static readonly double[] Identity = [1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0];

    // ---- synthetic model ----

    /// <summary>An element the drawable rules keep: a generic model that names its project phase.</summary>
    private static Element Product(int id, GeometryData? geometry) => new()
    {
        Id = id,
        CategoryId = -2000151,
        Category = "OST_GenericModel",
        Geometry = geometry,
        Parameters = [new Parameter { Id = -1012100, Value = Phase }],
        SerializedCreatedPhaseId = Phase,
    };

    private static RvtDocument Document(params Element[] elements) => RvtDocument.FromInfo(new RevitFileInfo { Elements = [.. elements] });

    private static GeoFace Face(double[][] ring, double[] normal, int node = 1) =>
        new() { OuterVertices = [.. ring], Normal = normal, GeometryNode = node };

    /// <summary>The six faces of the box [min, max], counter-clockwise seen from outside, normals outward.</summary>
    private static List<(double[][] Ring, double[] Normal)> BoxFaces(double[] min, double[] max)
    {
        double[] C(int x, int y, int z) => [x == 0 ? min[0] : max[0], y == 0 ? min[1] : max[1], z == 0 ? min[2] : max[2]];
        return
        [
            ([C(0, 0, 0), C(0, 1, 0), C(1, 1, 0), C(1, 0, 0)], [0, 0, -1]),
            ([C(0, 0, 1), C(1, 0, 1), C(1, 1, 1), C(0, 1, 1)], [0, 0, 1]),
            ([C(0, 0, 0), C(1, 0, 0), C(1, 0, 1), C(0, 0, 1)], [0, -1, 0]),
            ([C(0, 1, 0), C(0, 1, 1), C(1, 1, 1), C(1, 1, 0)], [0, 1, 0]),
            ([C(0, 0, 0), C(0, 0, 1), C(0, 1, 1), C(0, 1, 0)], [-1, 0, 0]),
            ([C(1, 0, 0), C(1, 1, 0), C(1, 1, 1), C(1, 0, 1)], [1, 0, 0]),
        ];
    }

    private static GRepData BoxBlock(double[] min, double[] max, int? material = null) => new()
    {
        BoundingBox = [.. min, .. max],
        Faces = BoxFaces(min, max).Select(face => new GRepFace
        {
            Vertices = [.. face.Ring],
            Origin = face.Ring[0],
            XVector = face.Normal,   // the decoder keeps the polygon normal there
            GeometryNode = 1,
            RenderStyleId = material,
        }).ToList(),
    };

    /// <summary>A family symbol (a box) and an instance that places it, built by the reader's own element builder; the instance
    /// is then made a drawable product.</summary>
    private static RvtDocument PlacedSymbol(double[] transform, double[]? instanceBox, GRepData? ownFaces = null)
    {
        var state = new ParsingState();
        state.GrepBlocks[SymbolId] = BoxBlock([0, 0, 0], [1, 2, 3]);
        var instance = ownFaces ?? new GRepData();
        instance.BoundingBox = instanceBox;
        instance.Instances = [new GRepInstance { SymbolElemId = SymbolId, Transform = transform }];
        state.GrepBlocks[InstanceId] = instance;
        return Built(state, SymbolId);
    }

    /// <summary>The symbols <paramref name="symbolIds"/> and then the instance, built from the blocks of <paramref name="state"/> by the
    /// reader's own element builder; the instance is then made a drawable product.</summary>
    private static RvtDocument Built(ParsingState state, params uint[] symbolIds)
    {
        var info = new RevitFileInfo();
        foreach (uint symbolId in symbolIds) ElementBuilder.BuildBlockOnlyElement(symbolId, state, info);
        ElementBuilder.BuildBlockOnlyElement(InstanceId, state, info);
        MakeProduct(info.Elements.Single(candidate => candidate.Id == InstanceId));
        return RvtDocument.FromInfo(info);
    }

    /// <summary>Makes a built element a drawable product: a generic model that names its project phase.</summary>
    private static void MakeProduct(Element element)
    {
        element.IsBlockOnly = false;
        element.CategoryId = -2000151;
        element.Category = "OST_GenericModel";
        element.Parameters.Add(new Parameter { Id = -1012100, Value = Phase });
        element.SerializedCreatedPhaseId = Phase;
    }

    /// <summary>A symbol block of one triangle at <paramref name="x"/> that places the symbols <paramref name="nested"/> where it
    /// stands.</summary>
    private static GRepData TriangleBlock(double x, params int[] nested) => new()
    {
        Faces = [new GRepFace { Vertices = [[x, 0, 0], [x + 1, 0, 0], [x, 1, 0]], Origin = [x, 0, 0], XVector = [0, 0, 1], GeometryNode = 1 }],
        Instances = nested.Select(id => new GRepInstance { SymbolElemId = id, Transform = Identity }).ToList(),
    };

    // ---- geometry checks ----

    private static IEnumerable<(double[] A, double[] B, double[] C, double[] Normal)> Triangles(TriangleMesh mesh)
    {
        double[] P(double[] values, int vertex) => [values[3 * vertex], values[3 * vertex + 1], values[3 * vertex + 2]];
        for (int index = 0; index < mesh.Indices.Length; index += 3)
            yield return (P(mesh.Positions, mesh.Indices[index]), P(mesh.Positions, mesh.Indices[index + 1]),
                P(mesh.Positions, mesh.Indices[index + 2]), P(mesh.Normals, mesh.Indices[index]));
    }

    private static double[] Cross(double[] a, double[] b, double[] c)
    {
        double ux = b[0] - a[0], uy = b[1] - a[1], uz = b[2] - a[2], vx = c[0] - a[0], vy = c[1] - a[1], vz = c[2] - a[2];
        return [uy * vz - uz * vy, uz * vx - ux * vz, ux * vy - uy * vx];
    }

    private static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];

    private static double Area(IEnumerable<TriangleMesh> meshes) =>
        meshes.SelectMany(Triangles).Sum(t => Math.Sqrt(Dot(Cross(t.A, t.B, t.C), Cross(t.A, t.B, t.C))) / 2);

    /// <summary>Every triangle is counter-clockwise around its (flat) vertex normal.</summary>
    private static void AssertWindingFollowsNormals(IEnumerable<TriangleMesh> meshes)
    {
        foreach (var (a, b, c, normal) in meshes.SelectMany(Triangles))
            Assert.True(Dot(Cross(a, b, c), normal) > 0);
    }

    // ---- triangulation ----

    [Fact]
    public void PlanarFaceWithHole_CoversTheRingAndLeavesTheHoleOpen()
    {
        var face = Face([[0, 0, 5], [4, 0, 5], [4, 4, 5], [0, 4, 5]], [0, 0, 1]);
        face.InnerVertices = [[[1, 1, 5], [1, 3, 5], [3, 3, 5], [3, 1, 5]]];

        var (meshes, failed) = ElementMeshes.Triangulate([face], _ => 0);

        Assert.Equal(0, failed);
        var mesh = Assert.Single(meshes);
        Assert.Equal(12, Area(meshes), 9);
        Assert.All(Triangles(mesh), t =>
        {
            double cx = (t.A[0] + t.B[0] + t.C[0]) / 3, cy = (t.A[1] + t.B[1] + t.C[1]) / 3;
            Assert.False(cx > 1 && cx < 3 && cy > 1 && cy < 3, "a triangle lies in the hole");
            Assert.Equal([0, 0, 1], t.Normal);
        });
        AssertWindingFollowsNormals(meshes);
    }

    [Fact]
    public void RingAgainstItsNormal_IsWoundAroundTheNormal()
    {
        // A mirrored placement keeps the order of the corners and maps the normal: the ring runs clockwise around it.
        var face = Face([[0, 0, 0], [0, 1, 0], [1, 1, 0], [1, 0, 0]], [0, 0, 1]);

        var (meshes, failed) = ElementMeshes.Triangulate([face], _ => 0);

        Assert.Equal(0, failed);
        Assert.Equal(1, Area(meshes), 12);
        AssertWindingFollowsNormals(meshes);
        Assert.All(meshes[0].Normals.Chunk(3), normal => Assert.Equal([0, 0, 1], normal));
    }

    [Fact]
    public void FaceWithoutAPolygon_IsCountedAsUntriangulated()
    {
        var (meshes, failed) = ElementMeshes.Triangulate([new GeoFace { Normal = [0, 0, 1] }], _ => 0);

        Assert.Empty(meshes);
        Assert.Equal(1, failed);
    }

    [Fact]
    public void FilledHole_IsCountedAsUntriangulated()
    {
        // A hole that crosses the outline cannot be cut out of it.
        var face = Face([[0, 0, 0], [4, 0, 0], [4, 4, 0], [0, 4, 0]], [0, 0, 1]);
        face.InnerVertices = [[[3, 1, 0], [3, 3, 0], [6, 3, 0], [6, 1, 0]]];

        var (_, failed) = ElementMeshes.Triangulate([face], _ => 0);

        Assert.Equal(1, failed);
    }

    [Fact]
    public void LargeCoordinates_KeepTheirPrecision()
    {
        const double far = 1e6;
        var face = Face([[far, far, 0], [far + 0.001, far, 0], [far + 0.001, far + 0.001, 0], [far, far + 0.001, 0]], [0, 0, 1]);

        var (meshes, failed) = ElementMeshes.Triangulate([face], _ => 0);

        Assert.Equal(0, failed);
        Assert.Equal(1e-6, Area(meshes), 10);
        Assert.Equal(far + 0.001, meshes[0].Positions.Where((_, index) => index % 3 == 0).Max());
    }

    [Fact]
    public void UntakenNodes_AreDroppedOnlyBesideTakenNodes()
    {
        var taken = Face([[0, 0, 0], [1, 0, 0], [1, 1, 0]], [0, 0, 1], node: 1);
        var untaken = Face([[0, 0, 1], [2, 0, 1], [2, 2, 1]], [0, 0, 1], node: -2);
        var unnumbered = Face([[0, 0, 2], [3, 0, 2], [3, 3, 2]], [0, 0, 1], node: 0);

        Assert.Equal(0.5 + 4.5, Area(ElementMeshes.Triangulate([taken, untaken, unnumbered], _ => 0).Meshes), 12);
        // Without a taken node the untaken one is all the element has.
        Assert.Equal(2, Area(ElementMeshes.Triangulate([untaken], _ => 0).Meshes), 12);
        // Beside a ghost node (exported, with no polygon here) the IFC export keeps the untaken nodes too.
        Assert.Equal(0.5 + 2 + 4.5, Area(ElementMeshes.Triangulate([taken, untaken, unnumbered], _ => 0, hasGhostNodes: true).Meshes), 12);
    }

    [Fact]
    public void ElementWithGhostNodes_KeepsItsUntakenNodesAndIsPartial()
    {
        var geometry = new GeometryData
        {
            Faces = [Face([[0, 0, 0], [1, 0, 0], [1, 1, 0]], [0, 0, 1], node: 1), Face([[0, 0, 5], [2, 0, 5], [2, 2, 5]], [0, 0, 1], node: -2)],
            GhostNodes = [(3, 0, 0)],
        };

        var element = Assert.Single(ElementMeshes.Extract(Document(Product(1, geometry))).Elements);

        Assert.Equal(0.5 + 2, Area(element.Meshes), 12);
        Assert.Equal(MeshCoverage.Partial, element.Coverage);
    }

    [Fact]
    public void SymbolPlacedInAnElementWithGhostNodes_IsNotShared()
    {
        // The symbol alone drops its untaken node; the element, which has a ghost node, keeps it.
        List<GeoFace> faces = [Face([[0, 0, 0], [1, 0, 0], [1, 1, 0]], [0, 0, 1], node: 1), Face([[0, 0, 5], [2, 0, 5], [2, 2, 5]], [0, 0, 1], node: -2)];
        var geometry = new GeometryData
        {
            Faces = faces,
            ExactMappedInstances = [new GeoInstance { SymbolId = 5, Transform = Identity }],
            GhostNodes = [(3, 0, 0)],
        };
        var symbol = new Element { Id = 5, Geometry = new GeometryData { Faces = faces } };

        var element = Assert.Single(ElementMeshes.Extract(Document(Product(1, geometry), symbol)).Elements);

        Assert.Empty(element.Placements);
        Assert.Equal(0.5 + 2, Area(element.Meshes), 12);
        Assert.Equal(MeshCoverage.Partial, element.Coverage);
    }

    // ---- materials ----

    [Fact]
    public void FaceMaterial_FollowsOwnThenNestedThenGraphicsStyle_OneMeshPerMaterial()
    {
        static GeoFace At(double z) => Face([[0, 0, z], [1, 0, z], [1, 1, z], [0, 1, z]], [0, 0, 1]);
        var own = At(0); own.RenderStyleId = 901; own.NestedRenderStyleId = 902;
        var nested = At(1); nested.NestedRenderStyleId = 902;
        var notAMaterial = At(2); notAMaterial.RenderStyleId = 800; notAMaterial.NestedRenderStyleId = 902;
        var styled = At(3); styled.GeometryStyle = 50;
        var styledWithoutMaterial = At(4); styledWithoutMaterial.GeometryStyle = 51;
        var none = At(5);
        var styledInvalid = At(6); styledInvalid.RenderStyleId = 800;
        var info = new RevitFileInfo
        {
            Elements =
            [
                Product(10, new GeometryData { Faces = [own, nested, notAMaterial, styled, styledWithoutMaterial, none, styledInvalid] }),
                new Element { Id = 901, CategoryId = -2000700 },
                new Element { Id = 902, CategoryId = -2000700 },
                new Element { Id = 903, CategoryId = -2000700 },
                new Element { Id = 800, CategoryId = -2000151 },
            ],
            GraphicsStyles = new Dictionary<int, (int Material, int Category)> { [50] = (903, -2000151), [51] = (-1, -2000151) },
        };

        var element = Assert.Single(ElementMeshes.Extract(RvtDocument.FromInfo(info)).Elements);

        Assert.Equal([901, 902, 903, 0], element.Meshes.Select(mesh => mesh.MaterialId));
        Assert.Equal([1.0, 2.0, 1.0, 3.0], element.Meshes.Select(mesh => Area([mesh])).Select(area => Math.Round(area, 9)));
    }

    // ---- placements ----

    [Fact]
    public void InstanceThatIsItsSymbolPlaced_IsAPlacement()
    {
        double[] rotated = [0, -1, 0, 1, 0, 0, 0, 0, 1, 10, 20, 30];
        var document = PlacedSymbol(rotated, [8, 20, 30, 10, 21, 33]);

        var set = ElementMeshes.Extract(document);

        var element = Assert.Single(set.Elements);
        Assert.Equal(InstanceId, element.ElementId);
        Assert.Empty(element.Meshes);
        Assert.Equal(MeshCoverage.Complete, element.Coverage);
        var placement = Assert.Single(element.Placements);
        Assert.Equal(SymbolId, placement.SymbolId);
        Assert.Equal(new MeshTransform(new(10, 20, 30), new(0, 1, 0), new(-1, 0, 0), new(0, 0, 1)), placement.Transform);
        // The symbol mesh is in the frame of the family, wound around its own normals.
        var symbol = set.Symbols[SymbolId];
        Assert.Equal(2 * (1 * 2 + 2 * 3 + 1 * 3), Area(symbol), 9);
        Assert.All(symbol.SelectMany(mesh => mesh.Positions), value => Assert.InRange(value, 0, 3));
        AssertWindingFollowsNormals(symbol);
        // Placed, it is the instance's own geometry.
        var own = ElementMeshes.Triangulate(document.GetElement(InstanceId)!.Geometry!.Faces, _ => 0).Meshes;
        Assert.Equal(Area(own), Area(symbol), 9);
    }

    [Fact]
    public void ClippedInstance_KeepsItsOwnShape()
    {
        // The tight box of the occurrence is lower than the symbol: the reader cuts the placed symbol to it.
        var document = PlacedSymbol(Identity, [0, 0, 0, 1, 2, 1.5]);
        Assert.True(document.GetElement(InstanceId)!.Geometry!.PlacedSymbolClipped);
        Assert.NotNull(document.GetElement(InstanceId)!.Geometry!.ExactMappedInstances);   // the IFC mapping flag alone would allow it

        var set = ElementMeshes.Extract(document);

        var element = Assert.Single(set.Elements);
        Assert.Empty(element.Placements);
        Assert.Empty(set.Symbols);
        Assert.Equal(1.5, element.Meshes.SelectMany(mesh => mesh.Positions).Where((_, index) => index % 3 == 2).Max());
        // The bottom and the four sides cut at the box; the cut leaves no top.
        Assert.Equal(1 * 2 + 2 * (1 * 1.5) + 2 * (2 * 1.5), Area(element.Meshes), 9);
    }

    [Fact]
    public void InstanceWithFacesOfItsOwn_KeepsItsOwnShape()
    {
        var own = new GRepData
        {
            Faces = [new GRepFace { Vertices = [[5, 5, 0], [6, 5, 0], [6, 6, 0]], Origin = [5, 5, 0], XVector = [0, 0, 1], GeometryNode = 1 }],
        };
        var document = PlacedSymbol(Identity, null, own);

        var element = Assert.Single(ElementMeshes.Extract(document).Elements);

        Assert.Empty(element.Placements);
        Assert.Equal(22 + 0.5, Area(element.Meshes), 9);
    }

    [Fact]
    public void ScaledInstance_KeepsItsOwnShape()
    {
        double[] scaled = [2, 0, 0, 0, 2, 0, 0, 0, 2, 0, 0, 0];
        var document = PlacedSymbol(scaled, null);

        var set = ElementMeshes.Extract(document);

        var element = Assert.Single(set.Elements);
        Assert.Empty(element.Placements);
        Assert.Empty(set.Symbols);
        Assert.Equal(4 * 22, Area(element.Meshes), 9);
    }

    [Fact]
    public void BoxSubstitute_IsReportedAndNeverPlaced()
    {
        // The reader replaces a large component at a rescaled placement by its box and says so.
        var state = new ParsingState();
        state.GrepBlocks[1] = new GRepData { Instances = [new GRepInstance { SymbolElemId = 2, Transform = [0.5, 0, 0, 0, 0.5, 0, 0, 0, 0.5, 0, 0, 0] }] };
        state.GrepBlocks[2] = BoxBlock([0, 0, 0], [4, 2, 2]);
        var placedBox = new StrongBox<bool>();
        Assert.NotNull(ElementBuilder.BuildPlacedSymbolGeometry(1, Identity, state, [], scaledComponentFaceBudget: 5, placedBox: placedBox));
        Assert.True(placedBox.Value);

        var geometry = new GeometryData
        {
            Faces = BoxFaces([0, 0, 0], [1, 1, 1]).Select(face => Face(face.Ring, face.Normal)).ToList(),
            ExactMappedInstances = [new GeoInstance { SymbolId = 5, Transform = Identity }],
            PlacedSymbolBox = true,
        };
        var symbol = new Element { Id = 5, Geometry = new GeometryData { Faces = BoxFaces([0, 0, 0], [1, 1, 1]).Select(face => Face(face.Ring, face.Normal)).ToList() } };

        var element = Assert.Single(ElementMeshes.Extract(Document(Product(1, geometry), symbol)).Elements);

        Assert.Equal(MeshCoverage.BoxSubstitute, element.Coverage);
        Assert.Empty(element.Placements);
        Assert.Equal(6, Area(element.Meshes), 9);
    }

    [Fact]
    public void MirroredPlacement_CarriesTheMirrorAndKeepsTheSymbolWinding()
    {
        double[] mirrored = [-1, 0, 0, 0, 1, 0, 0, 0, 1, 5, 0, 0];
        var document = PlacedSymbol(mirrored, [4, 0, 0, 5, 2, 3]);

        var set = ElementMeshes.Extract(document);

        var placement = Assert.Single(Assert.Single(set.Elements).Placements);
        Assert.Equal(-1, placement.Transform.Determinant, 12);
        // The shared triangles are those of the family, wound around the family's outward normals; the mirror is left to the
        // consumer, who reverses the winding for a negative determinant.
        var symbol = set.Symbols[SymbolId];
        AssertWindingFollowsNormals(symbol);
        Assert.All(symbol.SelectMany(Triangles), t =>
        {
            double[] centre = [0.5, 1, 1.5];
            double[] centroid = [(t.A[0] + t.B[0] + t.C[0]) / 3, (t.A[1] + t.B[1] + t.C[1]) / 3, (t.A[2] + t.B[2] + t.C[2]) / 3];
            Assert.True(Dot(t.Normal, [centroid[0] - centre[0], centroid[1] - centre[1], centroid[2] - centre[2]]) > 0);
        });
    }

    [Fact]
    public void MirroredInstanceWithItsOwnTriangles_IsWoundAroundTheMirroredNormals()
    {
        // The reader places the faces of a mirrored instance without changing the order of the corners, so its rings run
        // against the mapped normals; the own triangles follow the normals, which point out of the mirrored box.
        double[] mirrored = [-1, 0, 0, 0, 1, 0, 0, 0, 1, 5, 0, 0];
        var document = PlacedSymbol(mirrored, [4, 0, 0, 5, 2, 2]);   // clipped: the instance keeps its own shape

        var element = Assert.Single(ElementMeshes.Extract(document).Elements);

        Assert.Empty(element.Placements);
        AssertWindingFollowsNormals(element.Meshes);
        double[] centre = [4.5, 1, 1];
        Assert.All(element.Meshes.SelectMany(Triangles), t =>
        {
            double[] centroid = [(t.A[0] + t.B[0] + t.C[0]) / 3, (t.A[1] + t.B[1] + t.C[1]) / 3, (t.A[2] + t.B[2] + t.C[2]) / 3];
            Assert.True(Dot(t.Normal, [centroid[0] - centre[0], centroid[1] - centre[1], centroid[2] - centre[2]]) > 0);
        });
    }

    [Fact]
    public void SymbolPlacementsOff_GivesEveryElementItsOwnTriangles()
    {
        var document = PlacedSymbol(Identity, null);

        var set = ElementMeshes.Extract(document, new ElementMeshOptions { UseSymbolPlacements = false });

        var element = Assert.Single(set.Elements);
        Assert.Empty(element.Placements);
        Assert.Empty(set.Symbols);
        Assert.Equal(22, Area(element.Meshes), 9);
    }

    [Fact]
    public void SymbolWhoseOwnNestedSymbolWasClipped_IsNotShared()
    {
        var geometry = new GeometryData
        {
            Faces = BoxFaces([0, 0, 0], [1, 1, 1]).Select(face => Face(face.Ring, face.Normal)).ToList(),
            ExactMappedInstances = [new GeoInstance { SymbolId = 5, Transform = Identity }],
        };
        var symbol = new Element
        {
            Id = 5,
            Geometry = new GeometryData
            {
                Faces = BoxFaces([0, 0, 0], [1, 1, 1]).Select(face => Face(face.Ring, face.Normal)).ToList(),
                PlacedSymbolClipped = true,
            },
        };

        var element = Assert.Single(ElementMeshes.Extract(Document(Product(1, geometry), symbol)).Elements);

        Assert.Empty(element.Placements);
        Assert.Equal(6, Area(element.Meshes), 9);
    }

    [Fact]
    public void SymbolOfUntakenNodesBesideTakenOnes_IsNotShared()
    {
        // Alone, the symbol keeps its untaken nodes; in the element beside taken ones they are dropped.
        var hidden = BoxFaces([0, 0, 0], [1, 1, 1]).Select(face => Face(face.Ring, face.Normal, node: -1)).ToList();
        var geometry = new GeometryData
        {
            Faces = [.. hidden, Face([[5, 5, 5], [6, 5, 5], [6, 6, 5]], [0, 0, 1], node: 2)],
            ExactMappedInstances = [new GeoInstance { SymbolId = 5, Transform = Identity }, new GeoInstance { SymbolId = 6, Transform = Identity }],
        };
        var symbolA = new Element { Id = 5, Geometry = new GeometryData { Faces = hidden } };
        var symbolB = new Element { Id = 6, Geometry = new GeometryData { Faces = [Face([[5, 5, 5], [6, 5, 5], [6, 6, 5]], [0, 0, 1], node: 1)] } };

        var element = Assert.Single(ElementMeshes.Extract(Document(Product(1, geometry), symbolA, symbolB)).Elements);

        Assert.Empty(element.Placements);
        Assert.Equal(0.5, Area(element.Meshes), 12);
    }

    // ---- coverage and selection ----

    [Fact]
    public void UnresolvedSymbol_IsPartial()
    {
        var geometry = new GeometryData { Faces = [Face([[0, 0, 0], [1, 0, 0], [1, 1, 0]], [0, 0, 1])], UnresolvedSymbol = true };

        var element = Assert.Single(ElementMeshes.Extract(Document(Product(1, geometry))).Elements);

        Assert.Equal(MeshCoverage.Partial, element.Coverage);
        Assert.Equal(0, element.UntriangulatedFaces);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SymbolNestedInThePlacedSymbolWithoutGeometry_IsPartial(bool useSymbolPlacements)
    {
        // The placed symbol draws its triangle; the symbol it places in turn has no geometry block.
        var state = new ParsingState();
        state.GrepBlocks[InstanceId] = new GRepData { Instances = [new GRepInstance { SymbolElemId = SymbolId, Transform = Identity }] };
        state.GrepBlocks[SymbolId] = TriangleBlock(0, 999);

        var element = Assert.Single(ElementMeshes.Extract(Built(state, SymbolId),
            new ElementMeshOptions { UseSymbolPlacements = useSymbolPlacements }).Elements);

        Assert.Equal(MeshCoverage.Partial, element.Coverage);
        Assert.Equal(useSymbolPlacements ? 1 : 0, element.Placements.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SymbolTwoLevelsDownWithoutGeometry_IsPartial(bool useSymbolPlacements)
    {
        var state = new ParsingState();
        state.GrepBlocks[InstanceId] = new GRepData { Instances = [new GRepInstance { SymbolElemId = SymbolId, Transform = Identity }] };
        state.GrepBlocks[SymbolId] = TriangleBlock(0, 3);
        state.GrepBlocks[3] = TriangleBlock(2, 999);

        var element = Assert.Single(ElementMeshes.Extract(Built(state, 3, SymbolId),
            new ElementMeshOptions { UseSymbolPlacements = useSymbolPlacements }).Elements);

        Assert.Equal(MeshCoverage.Partial, element.Coverage);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NestedSymbolsThatAllHaveGeometry_AreComplete(bool useSymbolPlacements)
    {
        var state = new ParsingState();
        state.GrepBlocks[InstanceId] = new GRepData { Instances = [new GRepInstance { SymbolElemId = SymbolId, Transform = Identity }] };
        state.GrepBlocks[SymbolId] = TriangleBlock(0, 3);
        state.GrepBlocks[3] = TriangleBlock(2);

        var set = ElementMeshes.Extract(Built(state, 3, SymbolId), new ElementMeshOptions { UseSymbolPlacements = useSymbolPlacements });

        var element = Assert.Single(set.Elements);
        Assert.Equal(MeshCoverage.Complete, element.Coverage);
        Assert.Equal(1, Area(useSymbolPlacements ? set.Symbols[SymbolId] : element.Meshes), 12);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TransformOnlyInstanceOfASymbolWithANestedSymbolWithoutGeometry_IsPartial(bool useSymbolPlacements)
    {
        // A family instance record with a placement and no geometry block of its own: its geometry is its symbol placed.
        var state = new ParsingState();
        state.GrepBlocks[SymbolId] = TriangleBlock(0, 999);
        state.Records[InstanceId] = new ElementRecord { ElemId = InstanceId };
        state.FamilyInstanceTransforms[InstanceId] = new FamilyInstanceData { SymbolId = SymbolId, Trf = Identity };
        var info = new RevitFileInfo();
        ElementBuilder.BuildBlockOnlyElement(SymbolId, state, info);
        ElementBuilder.BuildDiscoveredElement(InstanceId, state, info);
        MakeProduct(info.Elements.Single(candidate => candidate.Id == InstanceId));

        var element = Assert.Single(ElementMeshes.Extract(RvtDocument.FromInfo(info),
            new ElementMeshOptions { UseSymbolPlacements = useSymbolPlacements }).Elements);

        Assert.Equal(MeshCoverage.Partial, element.Coverage);
        Assert.Equal(useSymbolPlacements ? 1 : 0, element.Placements.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FaceLeftOutOfItsTrimTriangulation_IsPartial(bool useSymbolPlacements)
    {
        // The reader counts on the block a face whose trim triangulation ran out of its work budget: it has no polygon.
        var state = new ParsingState();
        state.GrepBlocks[InstanceId] = new GRepData { Instances = [new GRepInstance { SymbolElemId = SymbolId, Transform = Identity }] };
        var symbol = TriangleBlock(0);
        symbol.UntriangulatedFaces = 1;
        state.GrepBlocks[SymbolId] = symbol;

        var set = ElementMeshes.Extract(Built(state, SymbolId), new ElementMeshOptions { UseSymbolPlacements = useSymbolPlacements });

        var element = Assert.Single(set.Elements);
        Assert.Equal(MeshCoverage.Partial, element.Coverage);
        Assert.Equal(1, element.UntriangulatedFaces);
        Assert.Equal(useSymbolPlacements ? 1 : 0, element.Placements.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void InstanceFaceLeftOutOfItsTrimTriangulation_IsPartial(bool useSymbolPlacements)
    {
        // The instance places a symbol, but an own face of the instance itself was left out of trim triangulation.
        var state = new ParsingState();
        state.GrepBlocks[InstanceId] = new GRepData
        {
            Instances = [new GRepInstance { SymbolElemId = SymbolId, Transform = Identity }],
            UntriangulatedFaces = 1,
        };
        state.GrepBlocks[SymbolId] = TriangleBlock(0);

        var set = ElementMeshes.Extract(Built(state, SymbolId), new ElementMeshOptions { UseSymbolPlacements = useSymbolPlacements });

        var element = Assert.Single(set.Elements);
        Assert.Equal(MeshCoverage.Partial, element.Coverage);
        Assert.Equal(1, element.UntriangulatedFaces);
        Assert.Equal(useSymbolPlacements ? 1 : 0, element.Placements.Count);
    }

    [Fact]
    public void UntriangulatedFace_IsPartial()
    {
        var geometry = new GeometryData { Faces = [Face([[0, 0, 0], [1, 0, 0], [1, 1, 0]], [0, 0, 1]), new GeoFace()] };

        var element = Assert.Single(ElementMeshes.Extract(Document(Product(1, geometry))).Elements);

        Assert.Equal(MeshCoverage.Partial, element.Coverage);
        Assert.Equal(1, element.UntriangulatedFaces);
    }

    [Fact]
    public void GeometryRecordWithoutFaces_IsNoGeometry()
    {
        var element = Assert.Single(ElementMeshes.Extract(Document(Product(1, new GeometryData { BoundingBox = [0, 0, 0, 1, 1, 1] }))).Elements);

        Assert.Equal(MeshCoverage.NoGeometry, element.Coverage);
        Assert.Empty(element.Meshes);
    }

    [Fact]
    public void UndecodedDocument_ListsItsElementsAsUndecoded()
    {
        var info = new RevitFileInfo
        {
            WideLayoutNotRewritten = true,
            Elements = [Product(1, null), Product(2, null)],
        };
        info.Elements[0].BoundingBox = [0, 0, 0, 1, 1, 1];

        var set = ElementMeshes.Extract(RvtDocument.FromInfo(info));

        Assert.True(set.GeometryUndecoded);
        var element = Assert.Single(set.Elements);   // the other one has no box either
        Assert.Equal(1, element.ElementId);
        Assert.Equal(MeshCoverage.Undecoded, element.Coverage);
        Assert.Empty(element.Meshes);
    }

    [Fact]
    public void DefinitionsAndContainers_AreNotListed()
    {
        static GeometryData Triangle() => new() { Faces = [Face([[0, 0, 0], [1, 0, 0], [1, 1, 0]], [0, 0, 1])] };
        var blockOnly = Product(2, Triangle()); blockOnly.IsBlockOnly = true;
        var template = Product(3, Triangle()); template.IsGroupTemplate = true;
        var withoutPhase = Product(4, Triangle()); withoutPhase.SerializedCreatedPhaseId = null;
        var room = Product(5, Triangle()); room.Category = "OST_Rooms"; room.CategoryId = -2000160;
        var run = Product(6, Triangle());
        var stair = Product(7, new GeometryData
        {
            Faces = Triangle().Faces,
            ExactMappedInstances = [new GeoInstance { SymbolId = 6, Transform = Identity }],
        });

        var set = ElementMeshes.Extract(Document(Product(1, Triangle()), blockOnly, template, withoutPhase, room, run, stair));

        Assert.Equal([1, 6], set.Elements.Select(element => element.ElementId));
    }

    [Fact]
    public void PlacementOfADrawableElement_IsLeftToThatElement()
    {
        static List<GeoFace> Box() => BoxFaces([0, 0, 0], [1, 1, 1]).Select(face => Face(face.Ring, face.Normal)).ToList();
        var member = Product(6, new GeometryData { Faces = Box() });
        var symbol = new Element { Id = 5, Geometry = new GeometryData { Faces = Box() } };
        var container = Product(7, new GeometryData
        {
            Faces = [.. Box(), .. Box()],
            ExactMappedInstances = [new GeoInstance { SymbolId = 6, Transform = Identity }, new GeoInstance { SymbolId = 5, Transform = Identity }],
        });

        var set = ElementMeshes.Extract(Document(member, symbol, container));

        Assert.Equal([6, 7], set.Elements.Select(element => element.ElementId));
        Assert.Equal([5], set.Elements[1].Placements.Select(placement => placement.SymbolId));
        Assert.Equal([5], set.Symbols.Keys);
    }

    [Fact]
    public void Extract_RejectsANullDocument()
    {
        Assert.Throws<ArgumentNullException>(() => ElementMeshes.Extract(null!));
    }

    // ---- reader flags ----

    [Fact]
    public void ClipGeometryToBounds_ReportsWhetherItChangedTheFaces()
    {
        var source = new GeometryData { Faces = BoxFaces([0, 0, 0], [1, 1, 1]).Select(face => Face(face.Ring, face.Normal)).ToList() };

        Assert.NotNull(ElementBuilder.ClipGeometryToBounds(source, [0, 0, 0, 1, 1, 1], out bool inside));
        Assert.False(inside);
        Assert.NotNull(ElementBuilder.ClipGeometryToBounds(source, [0, 0, 0, 1, 1, 0.5], out bool cut));
        Assert.True(cut);
        Assert.Null(ElementBuilder.ClipGeometryToBounds(source, [5, 5, 5, 6, 6, 6], out bool away));
        Assert.True(away);
        Assert.Same(source, ElementBuilder.ClipGeometryToBounds(source, null, out bool noBox));
        Assert.False(noBox);
    }

    // ---- public types ----

    [Fact]
    public void MeshTransform_ReadsTheRowMajorInstanceTransform()
    {
        double[] t = [0, -1, 0, 1, 0, 0, 0, 0, 1, 10, 20, 30];
        var transform = MeshTransform.FromRowMajor(t);
        double[] p = [1, 2, 3];

        Assert.Equal(new MeshVector(
            t[9] + p[0] * t[0] + p[1] * t[1] + p[2] * t[2],
            t[10] + p[0] * t[3] + p[1] * t[4] + p[2] * t[5],
            t[11] + p[0] * t[6] + p[1] * t[7] + p[2] * t[8]), transform.Apply(new MeshVector(1, 2, 3)));
        Assert.Equal(1, transform.Determinant, 12);
        Assert.Equal(-1, MeshTransform.FromRowMajor([-1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0]).Determinant, 12);
        Assert.Throws<ArgumentException>(() => MeshTransform.FromRowMajor([1, 0, 0]));
    }

    [Fact]
    public void MeshSet_CanBeBuiltByAConsumer()
    {
        var mesh = new TriangleMesh { Positions = [0, 0, 0, 1, 0, 0, 0, 1, 0], Indices = [0, 1, 2], Normals = [0, 0, 1, 0, 0, 1, 0, 0, 1], MaterialId = 3 };
        var set = new MeshSet
        {
            Elements =
            [
                new ElementMesh { ElementId = 1, Meshes = [mesh], Coverage = MeshCoverage.Complete },
                new ElementMesh { ElementId = 2, Placements = [new SymbolPlacement(9, MeshTransform.FromRowMajor(Identity))] },
            ],
            Symbols = new Dictionary<int, IReadOnlyList<TriangleMesh>> { [9] = [mesh] },
        };

        Assert.Equal(2, set.Elements.Count);
        Assert.False(set.GeometryUndecoded);
        Assert.Same(mesh, set.Symbols[9][0]);
    }
}
