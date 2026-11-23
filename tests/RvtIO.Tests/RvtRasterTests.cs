using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using RvtIO.Print;
using Xunit;

namespace RvtIO.Tests;

/// <summary>The raster reader (src/RvtIO/Print/RvtRaster): a PNG decoded to the RGB raster the PDF writer embeds.
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

    // A PNG whose header and scanlines the test chooses: the bytes of every scanline (the filter byte first) are given
    // as they are stored before compression.
    private static byte[] RawPng(int width, int height, byte[] scanlines, byte colorType = 2, byte depth = 8,
        byte interlace = 0, int headerLength = 13, byte[]? palette = null, byte[]? transparency = null)
        => PngFile(width, height, zlib => zlib.Write(scanlines), colorType, depth, interlace, headerLength, palette,
            transparency);

    // An image of zero filter bytes and zero samples: every pixel black, however large, without holding the
    // scanlines in memory.
    private static byte[] ZeroPng(int width, int height, byte colorType)
    {
        int bytesPerPixel = colorType == 6 ? 4 : colorType == 2 ? 3 : 1;
        return PngFile(width, height, zlib =>
        {
            var block = new byte[1 << 20];
            for (long left = ((long)width * bytesPerPixel + 1) * height; left > 0; left -= block.Length)
                zlib.Write(block, 0, (int)Math.Min(left, block.Length));
        }, colorType, 8, 0, 13, null, null);
    }

    private static byte[] PngFile(int width, int height, Action<Stream> writeScanlines, byte colorType, byte depth,
        byte interlace, int headerLength, byte[]? palette, byte[]? transparency)
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
        var header = new byte[Math.Max(headerLength, 13)];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = depth;
        header[9] = colorType;
        header[12] = interlace;
        Chunk(output, "IHDR", header[..headerLength]);
        if (palette != null) Chunk(output, "PLTE", palette);
        if (transparency != null) Chunk(output, "tRNS", transparency);
        using var packed = new MemoryStream();
        using (var zlib = new ZLibStream(packed, CompressionLevel.SmallestSize, leaveOpen: true))
            writeScanlines(zlib);
        Chunk(output, "IDAT", packed.ToArray());
        Chunk(output, "IEND", []);
        return output.ToArray();
    }

    [Fact]
    public void DecodePng_ReversesEveryScanlineFilter()
    {
        // Two RGB pixels per row: Sub, Up, Average and Paeth, each over the row decoded before it.
        byte[] scanlines =
        [
            1, 10, 20, 30, 1, 2, 3,
            2, 1, 1, 1, 2, 2, 2,
            3, 4, 4, 4, 4, 4, 4,
            4, 0, 0, 0, 0, 0, 0,
        ];

        var image = RvtRaster.DecodePng(RawPng(2, 4, scanlines));

        Assert.Equal(
        [
            10, 20, 30, 11, 22, 33,
            11, 21, 31, 13, 24, 35,
            9, 14, 19, 15, 23, 31,
            9, 14, 19, 15, 23, 31,
        ], image!.Rgb);
    }

    [Fact]
    public void DecodePng_PaethFilterPicksTheLeftOrTheUpperLeftNeighbourWhenThatIsNearestTheEstimate()
    {
        // Left 100, above 10, upper left 10: the estimate equals the left neighbour.
        var left = RvtRaster.DecodePng(RawPng(3, 2, [0, 10, 10, 10, 4, 90, 0, 0], colorType: 0));
        Assert.Equal([.. Enumerable.Repeat((byte)10, 9), .. Enumerable.Repeat((byte)100, 9)], left!.Rgb);

        // Second pixel: left 100, above 0, upper left 50: the estimate equals the upper left neighbour.
        var upperLeft = RvtRaster.DecodePng(RawPng(2, 2, [0, 50, 0, 4, 50, 7], colorType: 0));
        Assert.Equal([50, 50, 50, 0, 0, 0, 100, 100, 100, 57, 57, 57], upperLeft!.Rgb);
    }

    [Fact]
    public void DecodePng_RejectsAScanlineFilterThatDoesNotExist()
    {
        Assert.Null(RvtRaster.DecodePng(RawPng(1, 1, [5, 1, 2, 3])));
    }

    [Theory]
    [InlineData(12)]
    [InlineData(14)]
    public void DecodePng_RejectsAHeaderThatIsNotThirteenBytes(int headerLength)
    {
        Assert.Null(RvtRaster.DecodePng(RawPng(1, 1, [0, 1, 2, 3], headerLength: headerLength)));
    }

    [Fact]
    public void DecodePng_RejectsASizeThatNoDecodedImageCouldHold()
    {
        // The scanlines are never read: the declared size alone is refused, before any buffer is allocated.
        Assert.Null(RvtRaster.DecodePng(RawPng(65535, 65535, [0, 1, 2, 3])));
        Assert.Null(RvtRaster.DecodePng(RawPng(int.MaxValue, 1, [0, 1, 2, 3])));
    }

    [Fact]
    public void DecodePng_RejectsMorePixelsThanTheRasterBudgetEvenWhenTheScanlinesFit()
    {
        // The raster is three bytes a pixel: 50 million grey pixels are 50 MB of scanlines but 150 MB of raster.
        Assert.NotNull(RvtRaster.DecodePng(ZeroPng(4, 4, colorType: 0)));
        Assert.Null(RvtRaster.DecodePng(ZeroPng(50_000_000, 1, colorType: 0)));
    }

    [Fact]
    public void DecodePng_RejectsMoreScanlineBytesThanTheBudgetEvenWhenThePixelsFit()
    {
        // 33.6 million RGBA pixels are under the pixel budget but their scanlines are over 128 MiB.
        Assert.NotNull(RvtRaster.DecodePng(ZeroPng(4, 4, colorType: 6)));
        Assert.Null(RvtRaster.DecodePng(ZeroPng(33_600_000, 1, colorType: 6)));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(-1, 1)]
    public void DecodePng_RejectsAnEmptyOrNegativeSize(int width, int height)
    {
        Assert.Null(RvtRaster.DecodePng(RawPng(width, height, [0, 1, 2, 3])));
    }

    [Fact]
    public void DecodePng_RejectsInterlacedImagesAndDepthsOtherThanEight()
    {
        Assert.Null(RvtRaster.DecodePng(RawPng(1, 1, [0, 1, 2, 3], interlace: 1)));
        Assert.Null(RvtRaster.DecodePng(RawPng(1, 1, [0, 1, 2, 3], depth: 16)));
        Assert.Null(RvtRaster.DecodePng(RawPng(1, 1, [0, 1, 2, 3], colorType: 4)));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    public void DecodePng_RejectsAColourTypeItCannotRead(byte colorType)
    {
        // Grey with alpha is a real PNG colour type; the scanlines are the size of one sample a pixel.
        Assert.Null(RvtRaster.DecodePng(RawPng(1, 1, [0, 5], colorType: colorType)));
    }

    [Fact]
    public void DecodePng_RejectsScanlinesLongerThanTheHeaderDeclares()
    {
        Assert.Null(RvtRaster.DecodePng(RawPng(1, 1, [0, 1, 2, 3, 99])));
        Assert.Null(RvtRaster.DecodePng(RawPng(1, 2, [0, 1, 2, 3])));
    }

    [Fact]
    public void DecodePng_RejectsAChunkThatRunsPastTheEndOfTheFile()
    {
        byte[] png = RawPng(1, 1, [0, 1, 2, 3]);
        // The length of the header chunk, right after the signature, claims more bytes than the file holds.
        BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(8), png.Length);

        Assert.Null(RvtRaster.DecodePng(png));
        BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(8), -1);
        Assert.Null(RvtRaster.DecodePng(png));
    }

    [Fact]
    public void DecodePng_RejectsAnyChunkThatRunsPastTheEndOfTheFile()
    {
        byte[] intact = RawPng(1, 1, [0, 1, 2, 3]);
        Assert.NotNull(RvtRaster.DecodePng(intact));

        // IEND is the last twelve bytes: it claims a data byte the file does not have.
        byte[] shortEnd = [.. intact];
        BinaryPrimitives.WriteInt32BigEndian(shortEnd.AsSpan(shortEnd.Length - 12), 1);
        Assert.Null(RvtRaster.DecodePng(shortEnd));

        // The image data follows the signature and the 25-byte header chunk: it claims more than the file holds.
        byte[] longData = [.. intact];
        BinaryPrimitives.WriteInt32BigEndian(longData.AsSpan(33), longData.Length);
        Assert.Null(RvtRaster.DecodePng(longData));
    }

    [Fact]
    public void DecodePng_CompositesAlphaOverWhiteAndReadsGreyAndPaletteImages()
    {
        // RGBA: a transparent pixel becomes white, an opaque one keeps its colour, half transparent black is mid grey.
        var rgba = RvtRaster.DecodePng(RawPng(3, 1, [0, 9, 9, 9, 0, 100, 50, 0, 255, 0, 0, 0, 128], colorType: 6));
        Assert.Equal([255, 255, 255, 100, 50, 0, 127, 127, 127], rgba!.Rgb);

        var grey = RvtRaster.DecodePng(RawPng(2, 1, [0, 7, 200], colorType: 0));
        Assert.Equal([7, 7, 7, 200, 200, 200], grey!.Rgb);

        // Palette: entry 0 is half transparent red, entry 1 has no alpha entry and stays opaque blue.
        byte[] palette = [255, 0, 0, 0, 0, 255];
        var indexed = RvtRaster.DecodePng(RawPng(2, 1, [0, 0, 1], colorType: 3, palette: palette, transparency: [128]));
        Assert.Equal([255, 127, 127, 0, 0, 255], indexed!.Rgb);
    }

    [Fact]
    public void DecodePng_RejectsAPaletteIndexThePaletteDoesNotHold()
    {
        byte[] palette = [255, 0, 0, 0, 0, 255];

        Assert.Null(RvtRaster.DecodePng(RawPng(1, 1, [0, 2], colorType: 3, palette: palette)));
        Assert.Null(RvtRaster.DecodePng(RawPng(1, 1, [0, 0], colorType: 3)));
    }

    [Fact]
    public void DecodePng_ReadsThePixels()
    {
        var png = MinimalPng(2, 1, [255, 0, 0, 0, 0, 255]);

        var image = RvtRaster.DecodePng(png);

        Assert.NotNull(image);
        Assert.Equal(2, image!.Width);
        Assert.Equal(1, image.Height);
        Assert.Equal([255, 0, 0, 0, 0, 255], image.Rgb);
    }

    [Fact]
    public void DecodePng_RejectsBytesThatAreNotAPng()
    {
        Assert.Null(RvtRaster.DecodePng([1, 2, 3, 4, 5, 6, 7, 8]));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void DecodePng_RejectsScanlinesThatDisagreeWithTheHeader(int declaredWidth)
    {
        byte[] png = MinimalPng(2, 1, [255, 0, 0, 0, 0, 255]);
        BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(16), declaredWidth);

        Assert.Null(RvtRaster.DecodePng(png));
    }
}
