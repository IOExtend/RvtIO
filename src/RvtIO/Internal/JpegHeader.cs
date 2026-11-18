namespace RvtIO.Internal;

internal readonly record struct JpegHeader(int Width, int Height, int Components)
{
    /// <summary>Reads the size and the components from the JPEG start-of-frame marker.</summary>
    internal static JpegHeader Read(byte[] jpeg)
    {
        ArgumentNullException.ThrowIfNull(jpeg);
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8) return default;
        int at = 2;
        while (at + 4 <= jpeg.Length)
        {
            if (jpeg[at] != 0xFF) { at++; continue; }
            byte marker = jpeg[at + 1];
            if (marker is 0xD8 or 0xD9 or 0x01 || marker is >= 0xD0 and <= 0xD7) { at += 2; continue; }
            int length = (jpeg[at + 2] << 8) | jpeg[at + 3];
            bool isFrame = marker is >= 0xC0 and <= 0xCF && marker is not 0xC4 and not 0xC8 and not 0xCC;
            if (isFrame && at + 9 < jpeg.Length)
            {
                int height = (jpeg[at + 5] << 8) | jpeg[at + 6];
                int width = (jpeg[at + 7] << 8) | jpeg[at + 8];
                int components = jpeg[at + 9];
                return new(width, height, components);
            }
            at += 2 + length;
        }
        return default;
    }
}
