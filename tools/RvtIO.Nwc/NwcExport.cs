using System.Globalization;
using System.Text;
using IOScene;
using NwcIO;
using NwcIO.Model;
using NwcVector = NwcIO.Model.Vector3d;
using SceneVector = IOScene.Vector3d;

namespace RvtIO.Nwc;

internal static class NwcExport
{
    // The optional metadata is written by NwcIO 0.14 and interpreted by the linked-cache reader.
    private const string MetreCacheOption = "IOExtend.NwcWriter.Scene/1";
    private const string LinkClass = "IOExtend.NwcWriter.Link";
    internal static NwcDocument Build(SceneDocument scene, string sourceName, long sourceLength, DateTime timestamp, bool native = false)
    {
        if (scene.Validate().FirstOrDefault(d => d.Severity == SceneSeverity.Error) is { } error)
            throw new InvalidDataException(error.Message);
        if (scene.Instances.Count == 0)
            throw new InvalidDataException("The model has no triangle geometry to export.");

        var root = new NwcNode { Id = 0, Name = sourceName, Class = "Model" };
        int nextNodeId = 1;
        var levels = new Dictionary<int, NwcNode>();
        foreach (var level in scene.Levels)
        {
            var node = AddNode(root, nextNodeId++, level.Name, "RvtIO.Level");
            if (level.Elevation is double elevation)
                AddText(node, "Revit", "Elevation (m)", elevation.ToString("R", CultureInfo.InvariantCulture));
            levels.Add(level.Id, node);
        }

        var nodes = new Dictionary<long, NwcNode>(scene.Elements.Count);
        var propertySets = scene.PropertySets.ToDictionary(set => set.Id);
        foreach (var element in scene.Elements)
        {
            string id = element.Id.ToString(CultureInfo.InvariantCulture);
            var parent = element.LevelId is int levelId ? levels[levelId] : root;
            var node = AddNode(parent, nextNodeId++, element.Name, "RvtIO.Element");
            nodes.Add(element.Id, node);

            if (native)
            {
                node.Class = "LcRevitElement";
                node.IfcGlobalId = element.IfcGuid;
            }
            AddText(node, "Revit", "Element ID", id);
            AddText(node, "Revit", "UniqueId", element.SourceId);
            AddText(node, "Revit", "IFC GlobalId", element.IfcGuid);
            AddText(node, "Revit", "Category", element.Category);
            AddText(node, "Revit", "Type", element.TypeName);
            foreach (int setId in element.PropertySetIds)
            {
                var set = propertySets[setId];
                foreach (var property in set.Properties)
                    AddText(node, $"Revit {set.Name} Property", property.Name, property.Value.Text);
            }
        }

        var geometries = scene.Geometries.ToDictionary(geometry => geometry.Id);
        var materials = native
            ? scene.Materials.ToDictionary(m => m.Id, m => new NwcMaterial
            {
                Id = m.Id,
                Name = m.Name,
                Color = new(m.Color.R / 255f, m.Color.G / 255f, m.Color.B / 255f, m.Color.A / 255f),
            })
            : null;
        var meshes = new List<NwcMesh>(scene.Instances.Count);
        foreach (var instance in scene.Instances)
        {
            var geometry = geometries[instance.GeometryId];
            if (geometry.Topology != SceneTopology.Triangles || geometry.Vertices.Count < 3)
                throw new InvalidDataException($"Geometry {geometry.Id} is not a triangle mesh.");

            var transform = instance.Transform ?? Transform3d.Identity;
            var linear = new Transform3d(transform.XAxis, transform.YAxis, transform.ZAxis, SceneVector.Zero);
            var vertices = geometry.Vertices.Select(point => ToNwc(linear.Apply(point))).ToArray();
            var indices = geometry.Indices.Count == 0
                ? Enumerable.Range(0, vertices.Length).ToArray()
                : geometry.Indices.ToArray();
            if (SceneVector.Dot(transform.XAxis, SceneVector.Cross(transform.YAxis, transform.ZAxis)) < 0)
            {
                for (int i = 0; i < indices.Length; i += 3)
                    (indices[i + 1], indices[i + 2]) = (indices[i + 2], indices[i + 1]);
            }

            int meshId = meshes.Count + 1;
            var node = nodes[instance.ElementId];
            if (!native)
            {
                node.Properties.Add(new NwcPropertyRecord
                {
                    FirstInt = node.Id, Tag = 52, Name = "Mesh ID", Class = LinkClass, ValueType = 2, IntValue = meshId,
                });
            }
            meshes.Add(new NwcMesh
            {
                Id = meshId,
                Name = string.Create(CultureInfo.InvariantCulture, $"{node.Name} [RVT {instance.ElementId}]"),
                Vertices = vertices,
                Indices = indices,
                Node = native ? node : null,
                Material = instance.MaterialId is int materialId ? materials?.GetValueOrDefault(materialId) : null,
                // The read-back tree also looks for transforms by node ID.
                Transform = new NwcTransform { Id = nextNodeId++, Translation = ToNwc(transform.Translation) },
            });
        }

        return NwcDocument.Create(
            fileInfo: new NwcFileInfo
            {
                SourcePath = sourceName,
                SourceByteSize = sourceLength,
                SourceTimestampUtc = timestamp,
                LoaderPlugin = "LcOpRvtIONwc",
                LengthUnit = NwcLengthUnit.Meters,
                Options = [MetreCacheOption],
            },
            meshes: meshes,
            rootNode: root);
    }

    internal static void Save(NwcDocument cache, string path, bool native = false)
    {
        string destination = Path.GetFullPath(path);
        string temporary = Path.Combine(Path.GetDirectoryName(destination)!, $"rvtio-nwc-{Guid.NewGuid():N}.tmp");
        var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        bool moved = false;
        try
        {
            using (stream)
            {
                if (native)
                {
#if MANAGED_NWC
                    NwcWriter.WriteNative(cache, stream);
#else
                    throw new InvalidOperationException("Native NWC export requires a build with the NwcIO project.");
#endif
                }
                else
                {
                    NwcWriter.Write(cache, stream);
                }
            }
            File.Move(temporary, destination);
            moved = true;
        }
        finally
        {
            if (!moved)
                File.Delete(temporary);
        }
    }

    private static NwcVector ToNwc(SceneVector point) => new(point.X, point.Y, point.Z);

    private static NwcNode AddNode(NwcNode parent, int id, string name, string type)
    {
        ValidateString(name, id);
        var node = new NwcNode
        {
            Id = id,
            Name = string.IsNullOrWhiteSpace(name) ? type : name,
            Class = type,
            Parent = parent,
        };
        parent.Children.Add(node);
        return node;
    }

    private static void AddText(NwcNode node, string group, string name, string? text)
    {
        if (string.IsNullOrEmpty(name) || text is null)
            return;

        ValidateString(group, node.Id);
        ValidateString(name, node.Id);
        ValidateString(text, node.Id);
        node.Properties.Add(new NwcPropertyRecord
        {
            FirstInt = node.Id,
            Tag = 52,
            Name = name,
            Class = group,
            ValueType = 4,
            Value = text,
        });
    }

    private static void ValidateString(string text, int nodeId)
    {
        const int maxBytes = 65_536; // NwcIO's property record scanner limit.
        if (Encoding.UTF8.GetByteCount(text) > maxBytes)
            throw new InvalidDataException($"Node {nodeId} has a name or property that exceeds the NWC reader's {maxBytes}-byte limit.");
    }
}
