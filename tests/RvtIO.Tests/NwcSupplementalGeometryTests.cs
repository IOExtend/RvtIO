using IOScene;
using RvtIO.BuiltIns;
using RvtIO.Meshes;
using RvtIO.Model;
using RvtIO.Parsing;
using RvtIO.Scene;
using Xunit;

namespace RvtIO.Tests;

public class NwcSupplementalGeometryTests
{
    [Fact]
    public void NativeSceneIncludesModelLineAndReinforcementButSkipsDetailAndSketchCurves()
    {
        var modelLine = Line(1, BuiltInCategory.OST_MEPSpaceSeparationLines, detail: false);
        var detail = Line(2, BuiltInCategory.OST_Lines, detail: true);
        var sketch = Line(3, BuiltInCategory.OST_SketchLines, detail: false);
        var bar = RebarReaderTests.Bar(4, systemId: 0);
        var document = RvtDocument.FromInfo(new RevitFileInfo { Elements = [modelLine, detail, sketch, bar] });

        Assert.Empty(RvtSceneReader.Read(document, new() { ModelKey = "model" }).Document.Elements);
        var scene = RvtSceneReader.Read(document, new() { ModelKey = "model", IncludeModelLinesAndReinforcement = true });

        Assert.Equal(new long[] { 1, 4 }, scene.Document.Elements.Select(item => item.Id));
        Assert.Equal(2, scene.ElementsWithGeometry);
        var linePlacement = Assert.Single(scene.Document.Instances, item => item.ElementId == 1);
        var geometry = Assert.Single(scene.Document.Geometries, item => item.Id == linePlacement.GeometryId);
        Assert.Equal(SceneTopology.Lines, geometry.Topology);
        var translation = linePlacement.Transform!.Value.Translation;
        Assert.Equal(0.3048, translation.X, 12);
        Assert.Equal(0.6096, translation.Y, 12);
        Assert.Equal(0.9144, translation.Z, 12);
        Assert.Collection(geometry.Vertices, point => Assert.Equal(Vector3d.Zero, point), point => Assert.Equal(0.6096, point.X, 12));
        var native = Nwc.NwcExport.Build(scene.Document, "model.rvt", 0, DateTime.UnixEpoch, native: true);
        Assert.Equal(new[] { 0, 1 }, Assert.Single(native.Meshes, m => m.LineIndices.Count > 0).LineIndices);
        Assert.NotEmpty(Assert.Single(native.Meshes, m => m.TriangleCount > 0).Indices);
    }

    [Fact]
    public void NativeExportKeepsTheStairIdAboveItsAlreadyDrawnComponents()
    {
        var triangle = new GeoFace
        {
            OuterVertices = [[0, 0, 0], [1, 0, 0], [1, 1, 0]],
            Normal = [0, 0, 1],
            GeometryNode = 1,
        };
        var run = Product(1, BuiltInCategory.OST_StairsRuns, "StairsRun", new() { Faces = [triangle] });
        var stair = Product(2, BuiltInCategory.OST_Stairs, "StairsElement", new()
        {
            Faces = [triangle],
            ExactMappedInstances = [new() { SymbolId = run.Id, Transform = [1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0] }],
        });
        var document = RvtDocument.FromInfo(new RevitFileInfo { Elements = [run, stair] });

        Assert.Equal(run.Id, Assert.Single(RvtSceneReader.Read(document, new() { ModelKey = "model" }).Document.Elements).Id);
        var scene = RvtSceneReader.Read(document, new() { ModelKey = "model", IncludeModelLinesAndReinforcement = true }).Document;
        Assert.Equal(stair.Id, scene.Elements.Single(e => e.Id == run.Id).ParentId);
        Assert.Single(scene.Instances);
        var native = Nwc.NwcExport.Build(scene, "model.rvt", 0, DateTime.UnixEpoch, native: true);
        var parent = Assert.Single(native.RootNode!.Children);
        var child = Assert.Single(parent.Children);
        Assert.Contains(parent.Properties, property => property.Name == "Element ID" && property.Value == "2");
        Assert.Contains(child.Properties, property => property.Name == "Element ID" && property.Value == "1");
        Assert.Same(child, Assert.Single(native.Meshes).Node);

        static Element Product(int id, BuiltInCategory category, string className, GeometryData geometry) => new()
        {
            Id = id,
            CategoryId = (int)category,
            Category = category.ToString(),
            NativeRootClassName = className,
            NativeRootClassChain = [className],
            Geometry = geometry,
            SerializedCreatedPhaseId = 3,
            Parameters = [new() { Id = -1012100, Value = 3 }],
        };
    }

    [Fact]
    public void NativeModelArcKeepsItsPlaneAndTessellatesTheCircularPath()
    {
        var arc = Line(1, BuiltInCategory.OST_Lines, detail: false);
        arc.Location = null;
        arc.NativeModelArc = new()
        {
            CurveType = "Arc",
            ArcCenter = [3, 5, 7],
            ArcRadius = 2,
            ArcXDirection = [0, 1, 0],
            ArcYDirection = [0, 0, 1],
            ArcStartParameter = 0,
            ArcEndParameter = Math.PI / 2,
            StartPoint = [3, 7, 7],
            EndPoint = [3, 5, 9],
            Length = Math.PI,
        };
        var document = RvtDocument.FromInfo(new RevitFileInfo { Elements = [arc] });
        Assert.Empty(RvtSceneReader.Read(document, new() { ModelKey = "model" }).Document.Elements);
        var scene = RvtSceneReader.Read(document, new() { ModelKey = "model", IncludeModelLinesAndReinforcement = true }).Document;
        var instance = Assert.Single(scene.Instances);
        var geometry = Assert.Single(scene.Geometries);
        Assert.Equal(SceneTopology.Lines, geometry.Topology);
        Assert.True(geometry.Vertices.Count > 2);
        var points = geometry.Vertices.Select(point => instance.Transform!.Value.Apply(point)).ToArray();
        Assert.Equal(new Vector3d(3 * 0.3048, 7 * 0.3048, 7 * 0.3048), points[0]);
        Assert.Equal(new Vector3d(3 * 0.3048, 5 * 0.3048, 9 * 0.3048), points[^1]);
        Assert.All(points, point => Assert.Equal(3 * 0.3048, point.X, 12));
        for (int i = 0; i < points.Length - 1; i++)
        {
            Assert.Equal(i, geometry.Indices[2 * i]);
            Assert.Equal(i + 1, geometry.Indices[2 * i + 1]);
            var midpoint = (points[i] + points[i + 1]) * 0.5;
            double radius = Math.Sqrt(Math.Pow(midpoint.Y - 5 * 0.3048, 2) + Math.Pow(midpoint.Z - 7 * 0.3048, 2));
            Assert.InRange(2 * 0.3048 - radius, 0, 0.001);
        }
        var native = Nwc.NwcExport.Build(scene, "model.rvt", 0, DateTime.UnixEpoch, native: true);
        Assert.Equal(geometry.Indices, Assert.Single(native.Meshes).LineIndices);
    }

    [Fact]
    public void NativeAreaReinforcementKeepsBothCopiesOfTheHostOutline()
    {
        var host = new Element
        {
            Id = 2,
            Geometry = new()
            {
                Faces = [new() { OuterVertices = [[1, 2, 3], [4, 2, 3], [1, 5, 3]], Normal = [0, 0, 1] }],
            },
        };
        var area = new Element
        {
            Id = 1,
            CategoryId = (int)BuiltInCategory.OST_AreaRein,
            Category = nameof(BuiltInCategory.OST_AreaRein),
            NativeRootClassName = "RebarSystem",
            NativeRootClassChain = ["RebarSystem"],
            SerializedHostId = host.Id,
            AreaReinforcementPlane = new([1, 2, 3], [1, 0, 0], [0, 1, 0]),
            BoundingBox = [1, 2, 3, 4, 5, 6],
        };
        var document = RvtDocument.FromInfo(new RevitFileInfo { Elements = [area, host] });
        Assert.Empty(RvtSceneReader.Read(document, new() { ModelKey = "model" }).Document.Elements);

        var scene = RvtSceneReader.Read(document, new() { ModelKey = "model", IncludeModelLinesAndReinforcement = true }).Document;
        Assert.Equal(area.Id, Assert.Single(scene.Elements).Id);
        var instance = Assert.Single(scene.Instances);
        var geometry = Assert.Single(scene.Geometries);
        Assert.Equal(SceneTopology.Lines, geometry.Topology);
        Assert.Equal(12, geometry.Indices.Count);
        var points = geometry.Vertices.Select(point => instance.Transform!.Value.Apply(point)).ToArray();
        Assert.Equal(6, points.Distinct().Count());
        Assert.Equal(new Vector3d(1 * 0.3048, 2 * 0.3048, 3 * 0.3048), points[0]);
        Assert.Equal(6 * 0.3048, points.Max(p => p.Z), 12);
        Assert.All(points.Chunk(2), pair => Assert.Equal(pair[0].Z, pair[1].Z));
        Assert.Equal(2, points.Chunk(2).Count(pair => pair[0].X != pair[1].X && pair[0].Y != pair[1].Y));
    }

    [Fact]
    public void SweepKeepsTheStoredRadiusAndExtentsOfAStraightBar()
    {
        var segment = new RebarSegment(false, [4, 5, 6], [0, 0, 1], [], 0, 0, 10);
        var mesh = ReinforcementMeshes.Build(new RebarBars(1, 0, 10, [[segment]], ModelDiameter: 0.5));
        Assert.NotNull(mesh);

        var points = mesh.Positions.Chunk(3).ToArray();
        Assert.Equal(3.75, points.Min(p => p[0]), 12);
        Assert.Equal(4.25, points.Max(p => p[0]), 12);
        Assert.Equal(4.75, points.Min(p => p[1]), 12);
        Assert.Equal(5.25, points.Max(p => p[1]), 12);
        Assert.Equal(6, points.Min(p => p[2]), 12);
        Assert.Equal(16, points.Max(p => p[2]), 12);
        Assert.All(mesh.Indices, i => Assert.InRange(i, 0, points.Length - 1));
    }

    private static Element Line(int id, BuiltInCategory category, bool detail) => new()
    {
        Id = id,
        NativeRootClassName = "CurveElem",
        NativeRootClassChain = ["CurveElem"],
        CategoryId = (int)category,
        Category = category.ToString(),
        SerializedOwnerDbViewId = detail ? 99 : -1,
        Parameters = [new() { Id = (int)BuiltInParameter.CURVE_IS_DETAIL, StorageType = StorageType.Integer, Value = detail ? 1 : 0 }],
        Location = new() { StartPoint = [1, 2, 3], EndPoint = [3, 2, 3], Length = 2 },
    };
}
