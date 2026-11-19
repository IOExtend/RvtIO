using IOScene;
using NwcIO;
using NwcIO.Model;
using RvtIO.Nwc;
using Xunit;
using NwcVector = NwcIO.Model.Vector3d;

namespace RvtIO.Tests;

public class NwcExportTests
{
    [Fact]
    public void NavisworksCommandOpensTheExactCacheItCreates()
    {
        string directory = Path.GetFullPath(Path.GetTempPath());
        string executable = Path.Combine(directory, "Navisworks Manage", "Roamer.exe");
        string ifc = Path.Combine(directory, "sample model.ifc");
        string cache = Path.ChangeExtension(ifc, ".nwc");
        string log = Path.Combine(directory, "native trace.log");

        var command = NavisworksExport.CreateStartInfo(executable, ifc, cache, log);

        Assert.False(command.UseShellExecute);
        Assert.Equal(executable, command.FileName);
        Assert.Equal(Path.GetDirectoryName(executable), command.WorkingDirectory);
        Assert.Equal(new[] { "-NoGui", "-log", log, "-CreateCache", ifc, "-OpenFile", cache, "-Exit" },
            command.ArgumentList);
    }

    [Fact]
    public void NavisworksModeRejectsAnExistingDestinationBeforeStartingTheApplication()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            string path = Path.Combine(directory.FullName, "existing.nwc");
            File.WriteAllText(path, "original");

            Assert.Equal(2, global::RvtIO.Nwc.Program.Main(["missing.rvt", path, "--navisworks", "missing.exe"]));
            Assert.Equal("original", File.ReadAllText(path));
            Assert.Equal(path, Assert.Single(Directory.GetFiles(directory.FullName)));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void RoundTripKeepsPlacedTrianglesAndRevitProperties()
    {
        var scene = new SceneDocument();
        scene.Models.Add(new() { Id = 1, Key = "test" });
        scene.Levels.Add(new() { Id = 90, ModelId = 1, Name = "First floor", Elevation = 3 });
        scene.Elements.Add(new()
        {
            Id = 42, ModelId = 1, LevelId = 90, Name = "Wall", Category = "Walls",
            SourceId = "revit-id", PropertySetIds = [1],
        });
        scene.PropertySets.Add(new()
        {
            Id = 1, Name = "Instance",
            Properties = [new() { Name = "Fire rating", Value = SceneValue.FromText("EI 60") }],
        });
        scene.Geometries.Add(new() { Id = 1, Vertices = [new(0.25, 0, 0), new(1, 0, 0), new(0, 1, 0)] });
        scene.Instances.Add(new()
        {
            ElementId = 42, GeometryId = 1,
            Transform = new(new(-2, 0, 0), new(0, 1, 0), new(0, 0, 1), new(10_000_000.125, 3, 4)),
        });
        scene.Instances.Add(new() { ElementId = 42, GeometryId = 1 });

        var directory = Directory.CreateTempSubdirectory();
        try
        {
            string path = Path.Combine(directory.FullName, "model.nwc");
            var timestamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            NwcExport.Save(NwcExport.Build(scene, "model.rvt", 1024, timestamp), path);

            var reopened = NwcDocument.Open(path, new() { Strict = true, LengthUnit = NwcLengthUnit.Meters });
            Assert.Equal("model.rvt", reopened.FileInfo.SourcePath);
            Assert.Equal(1024L, reopened.FileInfo.SourceByteSize);
            Assert.Equal(timestamp, reopened.FileInfo.SourceTimestampUtc);
            Assert.Contains("IOExtend.NwcWriter.Scene/1", reopened.FileInfo.Options);
            Assert.Equal(2, reopened.Meshes.Count);
            Assert.All(reopened.Meshes, mesh => Assert.Equal("Wall [RVT 42]", mesh.Name));
            Assert.Equal(new[] { 0, 2, 1 }, reopened.Meshes[0].Indices);
            Assert.Equal(new NwcVector(9_999_999.625, 3, 4), reopened.Meshes[0].GetWorldVertices()[0]);
            Assert.Equal(new[] { 0, 1, 2 }, reopened.Meshes[1].Indices);
            Assert.Equal(new NwcVector(0.25, 0, 0), reopened.Meshes[1].GetWorldVertices()[0]);
            Assert.DoesNotContain(reopened.Diagnostics, diagnostic => diagnostic.Code is "meshes-skipped" or "property-scan-failed");

            var level = Assert.Single(reopened.RootNode!.Children);
            Assert.Equal("First floor", level.Name);
            Assert.Null(level.Transform);
            var element = Assert.Single(level.Children);
            Assert.Null(element.Transform);
            Assert.Contains(element.Properties, p => p.Name == "Element ID" && p.Value == "42");
            Assert.Contains(element.Properties, p => p.Name == "UniqueId" && p.Value == "revit-id");
            Assert.Contains(element.Properties, p => p.Name == "Fire rating" && p.Value == "EI 60");
            Assert.Equal(new long?[] { 1, 2 }, element.Properties
                .Where(p => p.Class == "IOExtend.NwcWriter.Link" && p.Name == "Mesh ID")
                .Select(p => p.IntValue));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void BuildRejectsNamesBeyondTheNwcReadersUtf8Limit()
    {
        var scene = new SceneDocument();
        scene.AddMesh(new string('é', 32_769), [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0)], [0, 1, 2]);
        var error = Assert.Throws<InvalidDataException>(() =>
            NwcExport.Build(scene, "model.rvt", 0, DateTime.UnixEpoch));
        Assert.Contains("65536", error.Message);
    }

    [Fact]
    public void SaveFailurePreservesTheDestinationAndRemovesTheTemporaryFile()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            string path = Path.Combine(directory.FullName, "existing.nwc");
            File.WriteAllText(path, "original");
            Assert.ThrowsAny<IOException>(() => NwcExport.Save(new NwcDocumentBuilder().Build(), path));
            Assert.Equal("original", File.ReadAllText(path));

            var invalid = new NwcDocumentBuilder().AddMesh(new NwcMesh { Id = 1, Vertices = [], Indices = [] }).Build();
            Assert.Throws<ArgumentException>(() => NwcExport.Save(invalid, Path.Combine(directory.FullName, "invalid.nwc")));
            Assert.Equal(path, Assert.Single(Directory.GetFiles(directory.FullName)));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
