using System.Buffers.Binary;
using System.IO.Compression;
using OpenMcdf;
using RvtIO.Parsing;
using RvtIO.Pdf;

namespace RvtIO.Print;

/// <summary>Reads a raster image out of a Revit file and decodes a PNG to an RGB raster: Revit keeps the image files it
/// places in the partition streams, and the print writer embeds them with Flate compression.</summary>
internal static class RvtRaster
{
    /// <summary>Finds and decodes the PNG of the given size in the file's streams, or null when there is none.</summary>
    internal static PdfRasterImage? FindPng(string path, int width, int height)
    {
        try
        {
            using var root = RootStorage.OpenRead(path);
            var partitions = root.OpenStorage("Partitions");
            foreach (var entry in partitions.EnumerateEntries())
            {
                if (entry.Type != EntryType.Stream) continue;
                byte[]? raw = ReadStream(partitions, entry);
                if (raw == null) continue;
                foreach (var chunk in new ChunkDecompressor().DecompressAll(raw))
                {
                    for (int i = 0; i + 33 < chunk.Length; i++)
                    {
                        if (chunk[i] != 0x89 || chunk[i + 1] != 0x50 || chunk[i + 2] != 0x4E
                            || chunk[i + 3] != 0x47) continue;
                        if (chunk[i + 12] != (byte)'I' || chunk[i + 13] != (byte)'H'
                            || chunk[i + 14] != (byte)'D' || chunk[i + 15] != (byte)'R') continue;
                        int w = BinaryPrimitives.ReadInt32BigEndian(chunk.AsSpan(i + 16));
                        int h = BinaryPrimitives.ReadInt32BigEndian(chunk.AsSpan(i + 20));
                        if (w != width || h != height) continue;
                        int end = chunk.AsSpan(i).IndexOf("IEND"u8);
                        if (end < 0) continue;
                        var png = new byte[end + 8];
                        Array.Copy(chunk, i, png, 0, png.Length);
                        return DecodePng(png);
                    }
                }
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException
            or NotSupportedException or UnauthorizedAccessException)
        {
            return null;
        }
        return null;
    }

    /// <summary>Finds the JPEG that Revit stores in the block of the given element (class 0x0890) and returns it as an
    /// image the PDF writer embeds as it is. A rendering of a 3D view is stored that way, in the block of its image
    /// symbol's type.</summary>
    internal static PdfJpegImage? FindJpeg(string path, int elementId)
    {
        try
        {
            using var root = RootStorage.OpenRead(path);
            var partitions = root.OpenStorage("Partitions");
            foreach (var entry in partitions.EnumerateEntries())
            {
                if (entry.Type != EntryType.Stream) continue;
                byte[]? raw = ReadStream(partitions, entry);
                if (raw == null) continue;
                foreach (var chunk in Chunks(raw))
                {
                    int pos = 0;
                    while (pos + 16 <= chunk.Length)
                    {
                        uint eid = BinaryPrimitives.ReadUInt32LittleEndian(chunk.AsSpan(pos));
                        int size = BinaryPrimitives.ReadInt32LittleEndian(chunk.AsSpan(pos + 8));
                        uint classIdx = BinaryPrimitives.ReadUInt32LittleEndian(chunk.AsSpan(pos + 12));
                        if (eid == 0 || size <= 0 || pos + 16 + size > chunk.Length)
                        {
                            pos++;
                            continue;
                        }
                        if (eid != (uint)elementId || (classIdx & 0xFFFF) != 0x0890)
                        {
                            pos += 16 + size;
                            continue;
                        }
                        var payload = chunk.AsSpan(pos + 16, size);
                        int start = payload.IndexOf(new byte[] { 0xFF, 0xD8, 0xFF });
                        if (start < 0) return null;
                        var jpeg = payload[start..].ToArray();
                        // The end of the JPEG is its last end-of-image marker; anything after it is padding.
                        int end = jpeg.AsSpan().LastIndexOf(new byte[] { 0xFF, 0xD9 });
                        if (end < 0) return null;
                        Array.Resize(ref jpeg, end + 2);
                        return new PdfJpegImage(jpeg);
                    }
                }
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException
            or NotSupportedException or UnauthorizedAccessException)
        {
            return null;
        }
        return null;
    }

    /// <summary>Finds the logo of the file: the widest PNG whose aspect (four to eight to one) is that of a title block
    /// logo, decoded to an RGB raster. Null when the file has no such image.</summary>
    internal static PdfRasterImage? FindLogo(string path)
    {
        try
        {
            using var root = RootStorage.OpenRead(path);
            var partitions = root.OpenStorage("Partitions");
            byte[]? best = null;
            int bestWidth = 0;
            foreach (var entry in partitions.EnumerateEntries())
            {
                if (entry.Type != EntryType.Stream) continue;
                byte[]? raw = ReadStream(partitions, entry);
                if (raw == null) continue;
                foreach (var chunk in new ChunkDecompressor().DecompressAll(raw))
                {
                    for (int i = 0; i + 33 < chunk.Length; i++)
                    {
                        if (chunk[i] != 0x89 || chunk[i + 1] != 0x50 || chunk[i + 2] != 0x4E
                            || chunk[i + 3] != 0x47) continue;
                        if (chunk[i + 12] != (byte)'I' || chunk[i + 13] != (byte)'H'
                            || chunk[i + 14] != (byte)'D' || chunk[i + 15] != (byte)'R') continue;
                        int w = BinaryPrimitives.ReadInt32BigEndian(chunk.AsSpan(i + 16));
                        int h = BinaryPrimitives.ReadInt32BigEndian(chunk.AsSpan(i + 20));
                        if (w < 300 || h < 40 || w > 4000 || h > 600) continue;
                        double aspect = (double)w / h;
                        if (aspect is < 3.5 or > 8) continue;
                        int end = chunk.AsSpan(i).IndexOf("IEND"u8);
                        if (end < 0) continue;
                        if (w <= bestWidth) continue;
                        bestWidth = w;
                        best = new byte[end + 8];
                        Array.Copy(chunk, i, best, 0, best.Length);
                    }
                }
            }
            return best == null ? null : DecodePng(best);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException
            or NotSupportedException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Decodes a PNG (8 bits per channel, RGB, RGBA or palette, non-interlaced) to an RGB raster, alpha
    /// composited over white.</summary>
    internal static PdfRasterImage? DecodePng(byte[] png)
    {
        if (png.Length < 8 || png[0] != 0x89 || png[1] != 0x50 || png[2] != 0x4E || png[3] != 0x47)
            return null;
        int at = 8, width = 0, height = 0, depth = 0, colorType = 0;
        var idat = new MemoryStream();
        byte[]? palette = null;
        byte[]? alpha = null;
        while (at + 8 <= png.Length)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(at));
            string type = System.Text.Encoding.ASCII.GetString(png, at + 4, 4);
            int data = at + 8;
            if (length < 0 || data + length + 4 > png.Length) return null;
            if (type == "IHDR")
            {
                width = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(data));
                height = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(data + 4));
                depth = png[data + 8];
                colorType = png[data + 9];
                if (png[data + 12] != 0) return null;
            }
            else if (type == "PLTE") palette = png[data..(data + length)];
            else if (type == "tRNS") alpha = png[data..(data + length)];
            else if (type == "IDAT") idat.Write(png, data, length);
            else if (type == "IEND") break;
            at = data + length + 4;
        }
        if (width <= 0 || height <= 0 || depth != 8) return null;
        int bpp = colorType switch { 2 => 3, 6 => 4, 3 => 1, 0 => 1, _ => 0 };
        if (bpp == 0) return null;
        idat.Position = 0;
        byte[] pixels;
        using (var zlib = new ZLibStream(idat, CompressionMode.Decompress))
        using (var output = new MemoryStream())
        {
            zlib.CopyTo(output);
            pixels = output.ToArray();
        }
        int stride = width * bpp;
        var rows = new List<byte[]>(height);
        int pos = 0;
        byte[] previous = new byte[stride];
        for (int y = 0; y < height; y++)
        {
            if (pos + 1 + stride > pixels.Length) return null;
            byte filter = pixels[pos++];
            var row = new byte[stride];
            Array.Copy(pixels, pos, row, 0, stride);
            pos += stride;
            for (int x = 0; x < stride; x++)
            {
                int left = x >= bpp ? row[x - bpp] : 0;
                int up = previous[x];
                int upLeft = x >= bpp ? previous[x - bpp] : 0;
                row[x] = filter switch
                {
                    0 => row[x],
                    1 => (byte)(row[x] + left),
                    2 => (byte)(row[x] + up),
                    3 => (byte)(row[x] + (left + up) / 2),
                    _ => (byte)(row[x] + Paeth(left, up, upLeft)),
                };
            }
            rows.Add(row);
            previous = row;
        }

        var rgb = new byte[width * height * 3];
        for (int y = 0; y < height; y++)
        {
            var row = rows[y];
            for (int x = 0; x < width; x++)
            {
                byte r, g, b, a = 255;
                if (colorType == 3)
                {
                    int index = row[x];
                    if (palette == null || index * 3 + 2 >= palette.Length) return null;
                    r = palette[index * 3]; g = palette[index * 3 + 1]; b = palette[index * 3 + 2];
                    if (alpha != null && index < alpha.Length) a = alpha[index];
                }
                else if (colorType == 0)
                {
                    r = g = b = row[x];
                }
                else
                {
                    r = row[x * bpp]; g = row[x * bpp + 1]; b = row[x * bpp + 2];
                    if (bpp == 4) a = row[x * bpp + 3];
                }
                int target = (y * width + x) * 3;
                rgb[target] = (byte)((r * a + 255 * (255 - a)) / 255);
                rgb[target + 1] = (byte)((g * a + 255 * (255 - a)) / 255);
                rgb[target + 2] = (byte)((b * a + 255 * (255 - a)) / 255);
            }
        }
        return new PdfRasterImage(width, height, rgb);
    }

    /// <summary>The decompressed chunks of a stream; a stream that does not decode yields none.</summary>
    private static IEnumerable<byte[]> Chunks(byte[] raw)
    {
        IReadOnlyList<byte[]> chunks;
        try
        {
            chunks = new ChunkDecompressor().DecompressAll(raw);
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException
            or IndexOutOfRangeException or OverflowException)
        {
            yield break;
        }
        foreach (var chunk in chunks) yield return chunk;
    }

    private static byte[]? ReadStream(Storage partitions, EntryInfo entry)
    {
        try
        {
            long length = entry.Length;
            if (length <= 0 || length > 512L * 1024 * 1024) return null;
            using var stream = partitions.OpenStream(entry.Name);
            var raw = new byte[length];
            stream.ReadExactly(raw);
            return raw;
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or NotSupportedException
            or UnauthorizedAccessException or InvalidDataException)
        {
            return null;
        }
    }

    private static int Paeth(int left, int up, int upLeft)
    {
        int estimate = left + up - upLeft;
        int dl = Math.Abs(estimate - left), du = Math.Abs(estimate - up), dul = Math.Abs(estimate - upLeft);
        if (dl <= du && dl <= dul) return left;
        return du <= dul ? up : upLeft;
    }
}
