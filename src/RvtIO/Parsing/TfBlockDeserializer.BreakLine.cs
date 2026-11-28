namespace RvtIO.Parsing;

internal static partial class TfBlockDeserializer
{
    private const int BreakReferenceCount = 20;

    internal sealed record BreakLineProfile(IReadOnlyList<(double X, double Y)> Line, double MaskY);

    // The computed M_Break Line symbol's reference faces carry the stretched extents and the
    // reference planes for the six vertices. The profile is used only for that family.
    internal static BreakLineProfile? ReadBreakReferencePlanes(ReadOnlySpan<byte> data)
    {
        const int planeSize = 105;
        if (data.Length < BreakReferenceCount * planeSize) return null;
        Span<double> x = stackalloc double[BreakReferenceCount];
        Span<double> y = stackalloc double[BreakReferenceCount];
        for (int i = 0; i < BreakReferenceCount; i++)
        {
            var plane = data.Slice(i * planeSize, planeSize);
            if (plane[32] != 1) return null;
            x[i] = BitConverter.ToDouble(plane[33..]);
            y[i] = BitConverter.ToDouble(plane[41..]);
            if (!(Math.Abs(x[i]) < 1000 && Math.Abs(y[i]) < 1000
                && Math.Abs(BitConverter.ToDouble(plane[49..])) < 1e-7)) return null;
        }
        if (!(x[0] < x[14] && x[14] < x[19] && x[19] < 0
            && 0 < x[18] && x[18] < x[15] && x[15] < x[2])) return null;
        if (!(Math.Abs(y[9]) < 1e-7 && Math.Abs(y[16] + y[17]) < 1e-7
            && Math.Abs(y[16]) > 1e-7 && Math.Abs(y[13]) > Math.Abs(y[16]))) return null;
        return new([(x[0], 0), (x[14], 0), (x[19], y[16]), (x[18], y[17]), (x[15], 0), (x[2], 0)], y[13]);
    }
}
