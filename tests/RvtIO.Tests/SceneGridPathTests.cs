using System.Text;
using IOScene;
using RvtIO.Meshes;
using RvtIO.Model;
using RvtIO.Parsing;
using RvtIO.Scene;
using Xunit;

namespace RvtIO.Tests;

public class SceneGridPathTests
{
    private const double Foot = 0.3048;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SceneExportsNativeArcsAndTheOrderedMixedAxis(bool wide)
    {
        var reader = new GridAxisReader(GridSchema(wide), new Dictionary<Guid, SchemaField[]?>());
        Read(100, 5, w => Grid(w, "Arc", 0, 8,
            [0, -1, Math.PI * 1.5, 1, -36, -30, 2.5, 1, 0, 0, 0, -1, 0, 0, 0, 1, 6]));
        Read(201, 5, w => Grid(w, "Mixed", 200, 7, [0, -1, 10, 1, 10, 0, 5, -1, 0, 0]));
        Read(202, 5, w => Grid(w, "Mixed", 200, 8,
            [-Math.PI / 2, -1, 0, 1, 10, 10, 5, 1, 0, 0, 0, 1, 0, 0, 0, 1, 10]));
        Read(203, 5, w => Grid(w, "Mixed", 200, 7, [0, -1, 10, 1, 26, 18, 5, -.6, -.8, 0]));
        Read(200, 9, w => Chain(w, "Mixed", [(2, 203), (0, 201), (1, 202)]));
        Read(300, 9, w => Chain(w, "Incomplete", [(0, 301), (1, 302)]));
        Read(301, 5, w => Grid(w, "Incomplete", 300, 7, [0, -1, 10, 1, 10, 0, 0, -1, 0, 0]));

        var (paths, members) = reader.Build();
        Assert.Equal(new[] { 201, 202, 203, 301, 302 }, members.Order());
        var info = new RevitFileInfo
        {
            CurvedGridAxes = paths,
            GridChainMemberIds = members,
            Elements = [LineElement(201, "Mixed"), LineElement(301, "Incomplete"), LineElement(400, "Straight")],
        };
        var scene = RvtSceneReader.Read(RvtDocument.FromInfo(info), new MeshSet(), new RvtSceneOptions { ModelKey = "grid.rvt" }).Document;
        var grid = Assert.Single(SceneJson.Read(SceneJson.Write(scene)).Grids);
        Assert.Equal("Straight", Assert.Single(grid.Lines).Name);
        Assert.Collection(grid.Paths!,
            arc =>
            {
                Assert.Equal("Arc", arc.Name);
                CheckPoint(arc.Points[0], -30, -30);
                CheckPoint(arc.Points[^1], -36, -24);
                Assert.Contains(arc.Points, point => point.X < -41 * Foot);
                CheckChordError(arc.Points, new(-36 * Foot, -30 * Foot, 0), 6 * Foot);
            },
            mixed =>
            {
                Assert.Equal("Mixed", mixed.Name);
                CheckPoint(mixed.Points[0], 0, 0);
                CheckPoint(mixed.Points[1], 10, 0);
                CheckPoint(mixed.Points[^2], 20, 10);
                CheckPoint(mixed.Points[^1], 26, 18);
                CheckChordError(mixed.Points.GetRange(1, mixed.Points.Count - 2), new(10 * Foot, 10 * Foot, 0), 10 * Foot);
                Assert.Equal(mixed.Points.Count, mixed.Points.Distinct().Count());
            });

        void Id(BinaryWriter w, int id)
        {
            if (wide) w.Write((long)id);
            else w.Write(id);
        }

        void Read(int id, ushort type, Action<BinaryWriter> payload)
        {
            using var bytes = new MemoryStream();
            using var writer = new BinaryWriter(bytes);
            writer.Write(type);
            Id(writer, id);
            payload(writer);
            reader.Read((uint)id, bytes.ToArray(), 0, (int)bytes.Length, type);
        }

        void Grid(BinaryWriter w, string name, int owner, ushort surface, double[] values)
        {
            Text(w, name);
            // The same surface class occurs twice. Only the queue entry owned by m_pSurface is the datum.
            Pointer(w, surface);
            Pointer(w, surface);
            Pointer(w, owner == 0 ? (ushort)0 : (ushort)11);
            foreach (double value in values) w.Write(0.0);
            foreach (double value in values) w.Write(value);
            if (owner != 0) Id(w, owner);
        }

        void Chain(BinaryWriter w, string label, (int Tag, int Member)[] entries)
        {
            Text(w, label);
            w.Write(entries.Length);
            foreach (var (tag, member) in entries)
            {
                w.Write(tag);
                Id(w, member);
            }
        }
    }

    private static Element LineElement(int id, string name) => new()
    {
        Id = id, Name = name, CategoryId = -2000220,
        Location = new LocationCurve { StartPoint = [0, 0, 0], EndPoint = [1, 0, 0] },
    };

    private static void CheckPoint(Vector3d point, double x, double y)
    {
        Assert.Equal(x * Foot, point.X, 10);
        Assert.Equal(y * Foot, point.Y, 10);
        Assert.Equal(0, point.Z);
    }

    private static void CheckChordError(List<Vector3d> points, Vector3d center, double radius)
    {
        Assert.True(points.Count > 3);
        for (int i = 1; i < points.Count; i++)
        {
            Assert.Equal(radius, (points[i] - center).Length, 9);
            double sagitta = radius - ((points[i - 1] + points[i]) * .5 - center).Length;
            Assert.InRange(sagitta, 0, .001);
        }
    }

    private static void Text(BinaryWriter writer, string text)
    {
        writer.Write(text.Length);
        writer.Write(Encoding.Unicode.GetBytes(text));
    }

    private static void Pointer(BinaryWriter writer, ushort type)
    {
        writer.Write(type == 0 ? 0u : uint.MaxValue);
        if (type != 0) writer.Write(type);
    }

    private static SchemaDocument GridSchema(bool wide)
    {
        static SchemaField F(string name, byte type, byte mods = 0, int? count = null, int? target = null, List<SchemaField>? args = null) =>
            new(name, type, mods, 0, count, target, null, args);
        static SchemaClass C(int id, string name, int parent = 0, params SchemaField[] fields) =>
            new(id, name, parent, 0, fields.ToList(), []);
        return new SchemaDocument
        {
            Classes =
            [
                C(1, "Identifier", 0, F("m_id", wide ? (byte)11 : (byte)4)),
                C(2, "ElementId", 1),
                C(3, "Element", 0, F("m_id", 14, target: 2)),
                C(4, "DatumPlane", 3, F("m_text", 8, 96), F("m_pFace", 14, 1), F("m_pSurface", 14, 1)),
                C(5, "Grid", 4, F("m_pGridChainMembershipCell", 14, 1)),
                C(6, "Envelope", 0, F("m_corners", 13, 16, 2, args: [F("", 7, 16, 2)])),
                C(7, "Plane", 6, F("m_origin", 7, 16, 3), F("m_xVec", 7, 16, 3)),
                C(8, "CylSurf", 6, F("m_center", 7, 16, 3), F("m_xVec", 7, 16, 3), F("m_yVec", 7, 16, 3),
                    F("m_zVec", 7, 16, 3), F("m_radius", 7)),
                C(9, "GridChain", 3, F("commonText", 8, 96), F("m_curveTagToGridsInChain", 14, 80, target: 10)),
                C(10, "std::pair< int, ElementId >", 0, F("first", 4), F("second", 14, target: 2)),
                C(11, "GridChainMembershipCell", 0, F("m_gridChainId", 14, target: 2)),
            ],
        };
    }
}
