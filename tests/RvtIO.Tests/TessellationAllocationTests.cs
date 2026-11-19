using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

/// <summary>Randomized differential tests: the allocation-free grid clipping, the max of absolute values and the ring ordering of
/// <see cref="GRepParserV2"/> give the same result, bit for bit, as the implementations they replaced (<see cref="TessellationReference"/>),
/// also for NaN, infinity, ties and empty input.</summary>
public class TessellationAllocationTests
{
    private static readonly double[] Specials =
    [
        double.NaN, double.PositiveInfinity, double.NegativeInfinity, 0.0, -0.0, 1, -1, 1e-12, -1e-12, 1e7, 1e-300, double.MaxValue, double.Epsilon,
    ];

    /// <summary>A random number: mostly a small multiple of 0.25 (so that points sit exactly on sample lines and tie), sometimes any double, sometimes special.</summary>
    private static double Number(Random random, double nanRate)
    {
        double pick = random.NextDouble();
        if (pick < nanRate) return Specials[random.Next(Specials.Length)];
        if (pick < 0.7) return random.Next(-8, 9) * 0.25;
        if (pick < 0.85) return (random.NextDouble() - 0.5) * 8;
        return (random.NextDouble() - 0.5) * Math.Pow(10, random.Next(-14, 9));
    }

    private static long Bits(double value) => BitConverter.DoubleToInt64Bits(value);

    [Fact]
    public void MaxAbsIgnoringNaN_matches_Select_Abs_Max()
    {
        var random = new Random(1);
        for (int iteration = 0; iteration < 200_000; iteration++)
        {
            double rate = iteration % 3 == 0 ? 0.0 : iteration % 3 == 1 ? 0.2 : 0.7;
            double[] v = [.. Enumerable.Range(0, 6).Select(_ => Number(random, rate))];
            double expected = TessellationReference.MaxAbs(v[0], v[1], v[2], v[3], v[4], v[5]);
            double actual = GRepParserV2.MaxAbsIgnoringNaN(v[0], v[1], v[2], v[3], v[4], v[5]);
            Assert.Equal(Bits(expected), Bits(actual));
        }
    }

    [Fact]
    public void TrimBoundaryBoundsPreserveTheEdgeToleranceAndFirstMatch()
    {
        (double, double, double, double)[] edges = [(0, 0, 1, 0), (1e6, 3, 1e6 + 1, 3), (1e6, 3, 1e6 + 1, 3)];
        (double[], double[])[] world = [([0, 0, 0], [1, 0, 0]), ([0, 0, 10], [2, 0, 10]), ([0, 0, 20], [2, 0, 20])];
        var point = GRepParserV2.TrimBoundaryEvaluator(edges, (u, v) => [u, v, -1], world);

        Assert.Equal(new[] { 1.0, 0, 10 }, point(1e6 + 0.5, 3 + 1e-8));
        // The large edge raises the pruning bound, while the small edge keeps its own tolerance.
        Assert.Equal(new[] { 0.5, 1e-9, -1 }, point(0.5, 1e-9));
    }

    [Fact]
    public void MaxIgnoringNaN_and_MinPropagatingNaN_match_Max_and_Min_of_a_projection()
    {
        var random = new Random(2);
        for (int iteration = 0; iteration < 100_000; iteration++)
        {
            int count = random.Next(1, 9);
            double rate = iteration % 3 == 0 ? 0.0 : iteration % 3 == 1 ? 0.2 : 0.7;
            var points = Enumerable.Range(0, count).Select(_ => new[] { Number(random, rate), Number(random, rate) }).ToList();
            double[] flat = [.. points.SelectMany(point => point)];
            for (int component = 0; component < 2; component++)
            {
                Assert.Equal(Bits(points.Max(point => point[component])), Bits(GRepParserV2.MaxIgnoringNaN(flat, count, component)));
                Assert.Equal(Bits(points.Min(point => point[component])), Bits(GRepParserV2.MinPropagatingNaN(flat, count, component)));
            }
        }
    }

    private static List<double[]> Polygon(Random random, double nanRate)
    {
        int count = random.Next(0, 3) == 0 ? random.Next(0, 4) : random.Next(3, 13);
        var polygon = new List<double[]>();
        for (int index = 0; index < count; index++)
        {
            // a repeated or closing point is common in the source polygons
            if (index > 0 && random.Next(8) == 0) polygon.Add([polygon[^1][0], polygon[^1][1]]);
            else polygon.Add([Number(random, nanRate), Number(random, nanRate)]);
        }
        return polygon;
    }

    private static List<double> Samples(Random random, double nanRate)
    {
        var samples = new List<double>();
        int count = random.Next(0, 7);
        double value = random.Next(-8, 3) * 0.25;
        for (int index = 0; index < count; index++)
        {
            samples.Add(random.NextDouble() < nanRate ? Specials[random.Next(Specials.Length)] : value);
            value += random.Next(0, 4) * 0.25; // equal neighbours happen
        }
        return samples;
    }

    private static string Describe(List<List<double[]>> cells) =>
        string.Join("|", cells.Select(cell => string.Join(";", cell.Select(point => string.Join(",", point.Select(Bits))))));

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.04)]
    [InlineData(0.3)]
    public void ClipGridCells_matches_the_per_cell_list_implementation(double nanRate)
    {
        var random = new Random(3 + (int)(nanRate * 100));
        int nonEmpty = 0;
        for (int iteration = 0; iteration < 6000; iteration++)
        {
            var sources = Enumerable.Range(0, random.Next(0, 4)).Select(_ => Polygon(random, nanRate)).ToList();
            var uSamples = Samples(random, nanRate);
            var vSamples = Samples(random, nanRate);
            bool allVCells = random.Next(2) == 0;
            int evaluator = random.Next(3);
            double[] Evaluate(double u, double v) => evaluator switch
            {
                0 => [u * 1.5 + v, u - v * 0.25, u * v],
                1 => [u, v, Math.Sin(u) + Math.Cos(v)],
                _ => [u, v, u > 1 ? double.NaN : u], // some corners are not finite: the cell is dropped
            };
            var expected = TessellationReference.ClipGridCells(sources, uSamples, vSamples, allVCells, Evaluate);
            var actual = GRepParserV2.ClipGridCells(sources, uSamples, vSamples, allVCells, Evaluate);
            Assert.Equal(Describe(expected), Describe(actual));
            if (expected.Count > 0) nonEmpty++;
        }
        Assert.True(nonEmpty > 500, "the generator must produce cells, got " + nonEmpty);
    }

    [Fact]
    public void ClipGridCells_reentered_from_evaluate_keeps_both_results()
    {
        double[] square = [0, 0, 4, 0, 4, 4, 0, 4];
        List<double[]> Source() => [.. Enumerable.Range(0, 4).Select(i => new[] { square[2 * i], square[2 * i + 1] })];
        List<double> uSamples = [0, 1, 2, 3, 4], vSamples = [0, 2, 4];
        var inner = new List<List<double[]>> { Source() };
        List<List<double[]>>? nested = null;
        double[] Evaluate(double u, double v)
        {
            nested ??= GRepParserV2.ClipGridCells(inner, uSamples, vSamples, true, (a, b) => [a, b, 0]);
            return [u, v, 0];
        }
        var expected = TessellationReference.ClipGridCells([Source()], uSamples, vSamples, true, (a, b) => [a, b, 0]);
        var actual = GRepParserV2.ClipGridCells([Source()], uSamples, vSamples, true, Evaluate);
        Assert.Equal(Describe(expected), Describe(actual));
        Assert.Equal(Describe(expected), Describe(nested!));
    }

    /// <summary>A random graph of segments on a small lattice: shared corners, repeated and reversed segments, open chains, several components, rings that tie.</summary>
    private static List<(double[] A, double[] B)> Segments(Random random, double nanRate)
    {
        int size = random.Next(2, 6);
        bool flat = random.Next(3) != 0;
        double jitter = random.Next(3) == 0 ? 1e-7 : 0;
        double[] Corner(int x, int y)
        {
            double z = flat ? 0 : (x + y) % 2;
            double[] corner = [x * 0.5 + (random.NextDouble() - 0.5) * jitter, y * 0.5 + (random.NextDouble() - 0.5) * jitter, z];
            if (random.NextDouble() < nanRate) corner[random.Next(3)] = Specials[random.Next(Specials.Length)];
            return corner;
        }
        var segments = new List<(double[] A, double[] B)>();
        int count = random.Next(3, 4 + size * size * 2);
        for (int index = 0; index < count; index++)
        {
            int x = random.Next(size), y = random.Next(size);
            int dx = 0, dy = 0;
            switch (random.Next(5)) { case 0: dx = 1; break; case 1: dy = 1; break; case 2: dx = 1; dy = 1; break; case 3: dx = 1; dy = -1; break; default: break; }
            int x2 = Math.Clamp(x + dx, 0, size), y2 = Math.Clamp(y + dy, 0, size);
            var edge = (Corner(x, y), Corner(x2, y2));
            segments.Add(random.Next(2) == 0 ? edge : (edge.Item2, edge.Item1));
            if (random.Next(10) == 0) segments.Add(segments[^1]);
        }
        // a few whole squares, so that rings exist and tie
        for (int square = random.Next(0, 3); square > 0; square--)
        {
            int x = random.Next(size), y = random.Next(size);
            double[][] c = [Corner(x, y), Corner(x + 1, y), Corner(x + 1, y + 1), Corner(x, y + 1)];
            for (int side = 0; side < 4; side++) segments.Add((c[side], c[(side + 1) % 4]));
        }
        return segments;
    }

    private static string Describe(List<List<double[]>> rings, List<(double[] A, double[] B)> segments)
    {
        // a ring is a list of the very arrays of the input: record which input array each point is as well as its bits
        var ids = new Dictionary<double[], int>(ReferenceEqualityComparer.Instance);
        foreach (var (a, b) in segments) { ids.TryAdd(a, ids.Count); ids.TryAdd(b, ids.Count); }
        return string.Join("|", rings.Select(ring => string.Join(";", ring.Select(point => ids[point] + ":" + string.Join(",", point.Select(Bits))))));
    }

    private static string Run(Func<List<List<double[]>>> run, List<(double[] A, double[] B)> segments)
    {
        try { return Describe(run(), segments); }
        catch (Exception ex) { return ex.GetType().Name; }
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.02)]
    public void OrderRingsW_matches_the_linq_implementation(double nanRate)
    {
        var random = new Random(5 + (int)(nanRate * 100));
        int withRings = 0, multiRing = 0;
        for (int iteration = 0; iteration < 3000; iteration++)
        {
            var segments = Segments(random, nanRate);
            foreach (bool containing in new[] { false, true })
                foreach (bool largest in new[] { false, true })
                {
                    string expected = Run(() => TessellationReference.OrderRingsW(segments, containing, largest), segments);
                    string actual = Run(() => GRepParserV2.OrderRingsW(segments, containing, largest), segments);
                    Assert.Equal(expected, actual);
                    if (expected.Length > 0 && !expected.EndsWith("Exception")) { withRings++; if (expected.Contains('|')) multiRing++; }
                }
        }
        Assert.True(withRings > 2000 && multiRing > 200, $"the generator must produce rings: {withRings} with rings, {multiRing} with several");
    }

    [Fact]
    public void OrderRingsW_ties_between_components_keep_the_earlier_ring()
    {
        double[] P(double x, double y) => [x, y, 0];
        var segments = new List<(double[] A, double[] B)>();
        foreach (double offset in new[] { 0.0, 10.0 }) // two identical squares in two components
        {
            double[][] c = [P(offset, 0), P(offset + 1, 0), P(offset + 1, 1), P(offset, 1)];
            for (int side = 0; side < 4; side++) segments.Add((c[side], c[(side + 1) % 4]));
        }
        var rings = GRepParserV2.OrderRingsW(segments);
        Assert.Equal(2, rings.Count);
        Assert.Equal(Describe(TessellationReference.OrderRingsW(segments), segments), Describe(rings, segments));
    }

    [Fact]
    public void OrderRingsW_preserves_simple_components_beside_branches()
    {
        double[] P(double x, double y) => [x, y, 0];
        var a = P(0, 0); var b = P(1, 0); var c = P(1, 1); var d = P(0, 1);
        var e = P(3, 0); var f = P(4, 0); var g = P(4, 1); var h = P(3, 1);
        List<(double[] A, double[] B)> segments =
        [
            (b, P(2, -1)), (f, h), (c, b), (g, h), (d, c), (h, e), (a, d), (f, g), (b, a), (e, f), (c, d),
        ];
        foreach (bool containing in new[] { false, true })
        {
            var expected = TessellationReference.OrderRingsW(segments, containing);
            var actual = GRepParserV2.OrderRingsW(segments, containing);
            Assert.True(expected.Count >= 2);
            Assert.Equal(Describe(expected, segments), Describe(actual, segments));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OrderRingsW_preserves_the_first_surviving_edge_direction(bool containing)
    {
        double[] a = [0, 0, 0], b = [1, 0, 0], c = [1, 1, 0], d = [0, 1, 0], spur = [2, 1, 0];
        var ring = Assert.Single(GRepParserV2.OrderRingsW(
            [(c, spur), (c, b), (b, c), (a, d), (a, b), (d, c)], containing));

        Assert.Collection(ring, p => Assert.Same(c, p), p => Assert.Same(d, p),
            p => Assert.Same(a, p), p => Assert.Same(b, p));
    }
}
