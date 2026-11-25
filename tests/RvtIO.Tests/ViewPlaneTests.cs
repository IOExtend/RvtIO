using System.Buffers.Binary;
using RvtIO.BuiltIns;
using RvtIO.Internal;
using RvtIO.Model;
using RvtIO.Parsing;
using RvtIO.Print;
using Xunit;

namespace RvtIO.Tests;

/// <summary>The plane of a view (BlockParser.CollectViewPlanes) and the section cut built from it
/// (ElevationPrinter.Collect with a view that carries its plane).
/// </summary>
public class ViewPlaneTests
{
    private const double FeetToMillimetres = 304.8;

    private static byte[] ChunkWithViewBlock(uint eid, double ox, double oy, double oz, double scale,
        double[] vertical, double[] camera)
    {
        int payload = 160;
        var chunk = new byte[16 + payload];
        BinaryPrimitives.WriteUInt32LittleEndian(chunk, eid);
        BinaryPrimitives.WriteInt32LittleEndian(chunk.AsSpan(8), payload);
        BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(12), 0x000003F8);
        void Double(int offset, double value) =>
            BinaryPrimitives.WriteDoubleLittleEndian(chunk.AsSpan(16 + offset), value);
        Double(32, 0);
        Double(40, ox); Double(48, oy); Double(56, oz);
        Double(64, scale);
        Double(72, vertical[0]); Double(80, vertical[1]); Double(88, vertical[2]);
        Double(96, camera[0]); Double(104, camera[1]); Double(112, camera[2]);
        return chunk;
    }

    [Fact]
    public void CollectViewPlanes_ReadsThePlaneOfTheViewBlock()
    {
        var state = new ParsingState();
        var chunk = ChunkWithViewBlock(500, 10, -2, 6, 0.01, [0, 0, 1], [-1, 0, 0]);

        BlockParser.CollectViewPlanes(chunk, state);

        var plane = Assert.Contains(500u, state.ViewPlanes);
        Assert.Equal([10, -2, 6], plane.Origin);
        Assert.Equal([0, 0, 1], plane.Vert);
        // The stored vector points toward the camera; the view looks the other way.
        Assert.Equal([1, 0, 0], plane.ViewDir);
        Assert.Equal([0, -1, 0], plane.Horiz);
    }

    [Theory]
    [InlineData(-0.999, 0)]
    [InlineData(1.001, 1)]
    public void CollectViewPlanes_FindsTheFallbackAtTheLastPayloadOffset(double verticalZ, int prefix)
    {
        var state = new ParsingState();
        double[] values = [0, 1, 0, 10, -2, 6, 100, 0, 0, verticalZ, 1, 0, 0];
        byte[] chunk = new byte[16 + prefix + values.Length * sizeof(double)];
        BinaryPrimitives.WriteUInt32LittleEndian(chunk, 500);
        BinaryPrimitives.WriteInt32LittleEndian(chunk.AsSpan(8), chunk.Length - 16);
        for (int index = 0; index < values.Length; index++)
            BinaryPrimitives.WriteDoubleLittleEndian(chunk.AsSpan(16 + prefix + index * sizeof(double)), values[index]);

        BlockParser.CollectViewPlanes(chunk, state);

        var plane = Assert.Contains(500u, state.ViewPlanes);
        Assert.Equal([10, -2, 6], plane.Origin);
        Assert.Equal([0, 0, verticalZ], plane.Vert);
    }

    private static Element SectionView(int id, double[] origin)
    {
        return new Element
        {
            Id = id,
            CategoryId = (int)BuiltInCategory.OST_Views,
            NativeRootClassName = "DBViewSection",
            ViewOrigin = origin,
            ViewDirection = [1, 0, 0],
            ViewVerticalDirection = [0, 0, 1],
            ViewHorizontalDirection = [0, -1, 0],
        };
    }

    private static Element WallWithFace(int id, double x)
    {
        var face = new GeoFace
        {
            Origin = [x, 0, 0],
            XVector = [0, 1, 0],
            YVector = [0, 0, 1],
            Normal = [1, 0, 0],
            OuterVertices = [[x, 0, 0], [x, 4, 0], [x, 4, 3], [x, 0, 3]],
        };
        return new Element
        {
            Id = id,
            CategoryId = (int)BuiltInCategory.OST_Walls,
            Geometry = new GeometryData { Faces = [face] },
        };
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(12.5, false)]
    [InlineData(-7.75, false)]
    [InlineData(12.5, true)]
    public void Collect_SectionUsesTheSavedPlaneInModelUnits(double cutX, bool windowed)
    {
        var view = SectionView(900, [cutX, 2, 1.5]);
        var document = RvtDocument.FromInfo(new RevitFileInfo
        {
            Elements = { view, WallWithFace(100, cutX - 2), WallWithFace(200, cutX + 2) },
        });

        var content = ElevationPrinter.Collect(document, "section", planeView: view,
            window: windowed ? (4 * FeetToMillimetres, 3 * FeetToMillimetres) : null);

        Assert.NotNull(content);
        var face = Assert.Single(content!.Faces);
        Assert.Equal((cutX + 2) * FeetToMillimetres, face.Depth, 3);
        Assert.Equal(-4 * FeetToMillimetres, face.Points.Min(point => point.X), 3);
        Assert.Equal(0, face.Points.Max(point => point.X), 3);
        Assert.Equal(0, face.Points.Min(point => point.Y), 3);
        Assert.Equal(3 * FeetToMillimetres, face.Points.Max(point => point.Y), 3);
    }
}
