using OpenMcdf;
using RvtIO.Parsing;

internal static class PageTailProbe
{
    internal sealed record Layout(int Degree, int Lanes, int Rows, int CountBits, int PaddingCount, int? ContentBytesIfCounted);

    internal static object Read(string path, string streamPath)
    {
        using var root = RootStorage.OpenRead(path);
        Storage storage = root;
        string[] names = streamPath.Split('/');
        foreach (string name in names[..^1]) storage = storage.OpenStorage(name);
        using var stream = storage.OpenStream(names[^1]);
        long offset = stream.Length == 0 ? 0 : (stream.Length - 1) / PagedGzipStream.PageStride * PagedGzipStream.PageStride;
        stream.Position = offset;
        var tail = new byte[stream.Length - offset];
        stream.ReadExactly(tail);
        return new { path = streamPath, offset, length = tail.Length, layouts = Inspect(tail) };
    }

    internal static List<Layout> Inspect(ReadOnlySpan<byte> page)
    {
        var result = new List<Layout>();
        if (page.IsEmpty || page.Length > PagedGzipStream.PageStride) return result;
        ReadOnlySpan<(int Degree, int Generator)> codes =
            [(3, 0xB), (4, 0x19), (5, 0x29), (6, 0x61), (7, 0xC1), (9, 0x221), (11, 0xA01)];
        foreach (var (degree, generator) in codes)
        {
            int fullRows = (1 << degree) - 1;
            for (int lanes = 1; lanes <= 255; lanes += 2)
            {
                // Only the 65-lane form occurs with omitted rows. Length alone can match several codes.
                int rows = lanes == 65 ? page.Length * 8 / lanes : fullRows;
                int used = rows * lanes;
                if (rows <= degree || rows > fullRows || (used + 7) / 8 != page.Length) continue;
                if ((used & 7) != 0 && page[^1] >> (used & 7) != 0) continue;
                if (!HasParity(page, degree, generator, lanes, rows)) continue;

                int width = Math.Max(4, degree - 2);
                int countStart = (rows - degree) * lanes - width;
                if (countStart < 0) continue;
                int count = 0;
                for (int i = 0; i < width; i++) count |= Bit(page, countStart + i) << i;
                int end = countStart / 8 - count;
                bool zeroGap = end >= 0;
                for (int i = end * 8; zeroGap && i < countStart; i++) zeroGap = Bit(page, i) == 0;
                result.Add(new(degree, lanes, rows, width, count, zeroGap ? end : null));
            }
        }
        return result;
    }

    private static bool HasParity(ReadOnlySpan<byte> page, int degree, int generator, int lanes, int rows)
    {
        int high = 1 << degree;
        int dataRows = rows - degree;
        // Multiply by x for each omitted zero coefficient and for the generator's degree.
        int shifts = high - 1 - rows + degree;
        for (int lane = 0; lane < lanes; lane++)
        {
            int remainder = 0;
            for (int row = dataRows - 1; row >= -shifts; row--)
            {
                remainder <<= 1;
                if (row >= 0) remainder |= Bit(page, row * lanes + lane);
                if ((remainder & high) != 0) remainder ^= generator;
            }
            for (int i = 0; i < degree; i++)
                if (((remainder >> i) & 1) != Bit(page, (dataRows + i) * lanes + lane)) return false;
        }
        return true;
    }

    private static int Bit(ReadOnlySpan<byte> page, int bit) => (page[bit >> 3] >> (bit & 7)) & 1;
}
