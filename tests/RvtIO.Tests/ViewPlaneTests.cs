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

    private static Element SectionView(int id)
    {
        return new Element
        {
            Id = id,
            CategoryId = (int)BuiltInCategory.OST_Views,
            NativeRootClassName = "DBViewSection",
            ViewOrigin = [0, 0, 0],
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

    [Fact]
    public void Collect_SectionIsCutAtItsPlane()
    {
        // The plane is at x = 0 looking along +x: the wall at x = -2 stands between the viewer and the plane and is cut
        // away; the wall at x = 2 stands behind it and is drawn.
        var document = RvtDocument.FromInfo(new RevitFileInfo
        {
            Elements = { SectionView(900), WallWithFace(100, -2), WallWithFace(200, 2) },
        });

        var content = ElevationPrinter.Collect(document, "section", planeView: document.GetElement(900));

        Assert.NotNull(content);
        Assert.Single(content!.Faces);
        // The wall at x = 2 ft is drawn; it spans y from 0 to 4 ft, and the horizontal axis is -y.
        Assert.Equal(-4 * FeetToMillimetres, content!.MinX, 3);
        Assert.Equal(0, content.MaxX, 3);
    }
}
