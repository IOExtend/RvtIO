using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

/// <summary>The raster reader decodes PNG data to RGB pixels.
/// </summary>
public class RvtRasterTests
{
    private static byte[] MinimalPng(int width, int height, byte[] rgb)
    {
        static void Chunk(MemoryStream stream, string type, byte[] data)
        {
            Span<byte> length = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
            stream.Write(length);
            stream.Write(Encoding.ASCII.GetBytes(type));
            stream.Write(data);
            stream.Write([0, 0, 0, 0]);
        }
        using var output = new MemoryStream();
        output.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        using (var ihdr = new MemoryStream())
        {
            Span<byte> header = stackalloc byte[13];
            header.Clear();
            BinaryPrimitives.WriteInt32BigEndian(header, width);
            BinaryPrimitives.WriteInt32BigEndian(header[4..], height);
            header[8] = 8;
            header[9] = 2;
            ihdr.Write(header);
            Chunk(output, "IHDR", ihdr.ToArray());
        }
        using (var raw = new MemoryStream())
        {
            for (int y = 0; y < height; y++)
            {
                raw.WriteByte(0);
                raw.Write(rgb, y * width * 3, width * 3);
            }
            using var packed = new MemoryStream();
            raw.Position = 0;
            using (var zlib = new ZLibStream(packed, CompressionLevel.SmallestSize, leaveOpen: true))
                raw.CopyTo(zlib);
            Chunk(output, "IDAT", packed.ToArray());
        }
        Chunk(output, "IEND", []);
        return output.ToArray();
    }

    [Fact]
    public void DecodePng_ReadsThePixels()
    {
        var png = MinimalPng(2, 1, [255, 0, 0, 0, 0, 255]);

        var image = RvtRaster.DecodePng(png);

        Assert.NotNull(image);
        var (width, height, rgb) = image.Value;
        Assert.Equal(2, width);
        Assert.Equal(1, height);
        Assert.Equal([255, 0, 0, 0, 0, 255], rgb);
    }

    [Fact]
    public void DecodePng_RejectsBytesThatAreNotAPng()
    {
        Assert.Null(RvtRaster.DecodePng([1, 2, 3, 4, 5, 6, 7, 8]));
    }
}
