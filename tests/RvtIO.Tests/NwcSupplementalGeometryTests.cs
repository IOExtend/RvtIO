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
