using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

/// <summary>The numeric face corner key partitions faces exactly like the text key it replaced: two faces share a key
/// if and only if their corner multisets are equal (the key is only compared for equality).</summary>
public class FaceCornerKeyTests
{
    private static long Q(double v) => (long)Math.Round(v * 1e4);

    private static string Old(List<double[]> cs)
    {
        var parts = new string[cs.Count];
        for (int i = 0; i < cs.Count; i++) parts[i] = $"{Q(cs[i][0])},{Q(cs[i][1])},{Q(cs[i][2])}";
        Array.Sort(parts);
        return string.Join(";", parts);
    }

    private static List<double[]> Corners(Random random, int count) =>
        [.. Enumerable.Range(0, count).Select(_ => new[] { random.Next(-6, 7) * 0.37, random.Next(-6, 7) * 0.001, random.Next(-3, 4) * 123.456 })];

    [Fact]
    public void Same_equality_relation_as_the_culture_sorted_key_on_shuffled_and_perturbed_faces()
    {
        var random = new Random(11);
        for (int iteration = 0; iteration < 20_000; iteration++)
        {
            var a = Corners(random, random.Next(0, 7));
            var shuffled = a.OrderBy(_ => random.Next()).ToList();
            Assert.Equal(Old(a), Old(shuffled));
            Assert.Equal(ElementBuilder.FaceCornerKey(a), ElementBuilder.FaceCornerKey(shuffled));
            var b = Corners(random, random.Next(0, 7));
            Assert.Equal(Old(a) == Old(b), ElementBuilder.FaceCornerKey(a) == ElementBuilder.FaceCornerKey(b));
        }
    }

    [Theory]
    [InlineData(32)]
    [InlineData(33)]
    public void Repeated_corners_and_quantization_boundaries_survive_reordering(int count)
    {
        var ring = Enumerable.Range(0, count).Select(i => new[] { (i % 3 - 1) * 0.00005, -2d, 0d }).ToList();
        string key = ElementBuilder.FaceCornerKey(ring);
        ring.Reverse();
        Assert.Equal(key, ElementBuilder.FaceCornerKey(ring));
        ring[0][0] = 0.000051;
        Assert.NotEqual(key, ElementBuilder.FaceCornerKey(ring));
        ring.RemoveAt(0);
        Assert.NotEqual(key, ElementBuilder.FaceCornerKey(ring));
    }
}
