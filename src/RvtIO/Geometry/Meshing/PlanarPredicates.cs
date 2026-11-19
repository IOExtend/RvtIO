using System.Numerics;

namespace RvtIO.Geometry.Meshing;

internal static class PlanarPredicates
{
    internal static double Orientation(UvPoint a, UvPoint b, UvPoint c)
    {
        UvPoint u = a - c, v = b - c;
        double left = u.X * v.Y, right = u.Y * v.X, area = left - right;
        if (Math.Abs(area) > 1e-14 * (Math.Abs(left) + Math.Abs(right))) return area;
        var (n, exponent) = Exact(a, b, c);
        BigInteger exact = (n[0] - n[4]) * (n[3] - n[5]) - (n[1] - n[5]) * (n[2] - n[4]);
        return Math.ScaleB((double)exact, 2 * exponent);
    }

    internal static double Disk(UvPoint a, UvPoint b, UvPoint c, UvPoint d)
    {
        UvPoint u = a - d, v = b - d, w = c - d;
        double u2 = u.LengthSquared, v2 = v.LengthSquared, w2 = w.LengthSquared;
        double value = u2 * v.Cross(w) + v2 * w.Cross(u) + w2 * u.Cross(v);
        double bound = u2 * (Math.Abs(v.X * w.Y) + Math.Abs(v.Y * w.X))
                    + v2 * (Math.Abs(w.X * u.Y) + Math.Abs(w.Y * u.X))
                    + w2 * (Math.Abs(u.X * v.Y) + Math.Abs(u.Y * v.X));
        if (Math.Abs(value) > 1e-14 * bound) return value;
        var (n, _) = Exact(a, b, c, d);
        BigInteger ux = n[0] - n[6], uy = n[1] - n[7], vx = n[2] - n[6], vy = n[3] - n[7], wx = n[4] - n[6], wy = n[5] - n[7];
        return ((ux * ux + uy * uy) * (vx * wy - vy * wx) + (vx * vx + vy * vy) * (wx * uy - wy * ux) + (wx * wx + wy * wy) * (ux * vy - uy * vx)).Sign;
    }

    private static (BigInteger[] Values, int Exponent) Exact(params UvPoint[] p)
    {
        var numbers = new (long Significand, int Exp)[p.Length * 2];
        int exponent = 0;
        for (int i = 0; i < p.Length; i++)
        {
            numbers[i * 2] = Extract(p[i].X);
            numbers[i * 2 + 1] = Extract(p[i].Y);
            exponent = Math.Min(exponent, Math.Min(numbers[i * 2].Exp, numbers[i * 2 + 1].Exp));
        }
        return (numbers.Select(x => new BigInteger(x.Significand) << (x.Exp - exponent)).ToArray(), exponent);
    }

    private static (long Significand, int Exp) Extract(double p)
    {
        long bits = BitConverter.DoubleToInt64Bits(p);
        int exp = (int)((bits >> 52) & 2047);
        long sig = bits & ((1L << 52) - 1);
        if (exp != 0) sig |= 1L << 52;
        if (sig == 0) return (0, 0);
        return (bits < 0 ? -sig : sig, (exp == 0 ? 1 : exp) - 1075);
    }
}
