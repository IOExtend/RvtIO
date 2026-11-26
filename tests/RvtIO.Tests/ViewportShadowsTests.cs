using System.Numerics;
using RvtIO.Model;
using RvtIO.Print;
using Xunit;

namespace RvtIO.Tests;

public class ViewportShadowsTests
{
    [Fact]
    public void ShadesSurfacesFacingAwayFromTheSunEvenWithNoCasters()
    {
        var camera = new ViewCropFrame(0, 0, 10, [1, 0, 0], [0, 1, 0], [0, 0, -1], -10, -10, 10, 10, false);
        var shadows = new ViewportShadows(camera, Vector3.UnitZ, 50);
        byte[] rgb = [160, 80, 40, 160, 80, 40, 255, 255, 255];

        shadows.Apply(rgb, [10, 10, double.PositiveInfinity], [false, true, false], 3, 1, 0, 1, 1, 1, 0, 0, 1);

        Assert.Equal(new byte[] { 80, 40, 20, 160, 80, 40, 255, 255, 255 }, rgb);
    }

    [Fact]
    public void CastsOntoTheReceiverAndLeavesTheOpeningAndCoplanarFaceLit()
    {
        var camera = new ViewCropFrame(0, 0, 10, [1, 0, 0], [0, 1, 0], [0, 0, -1], -10, -10, 10, 10, false);
        var shadows = new ViewportShadows(camera, Vector3.UnitZ, 50);
        shadows.AddCaster(
        [
            [[-2, -2, 2], [2, -2, 2], [2, 2, 2], [-2, 2, 2]],
            [[-0.75, -0.75, 2], [-0.75, 0.75, 2], [0.75, 0.75, 2], [0.75, -0.75, 2]],
        ], [0, 0, 1]);
        var rgb = Enumerable.Repeat((byte)128, 18).ToArray();

        shadows.Apply(rgb, [10, 10, 10, 10, 8, 10], [true, true, true, true, true, true],
            6, 1, -3, 0.5, 1, 1, 0, 0, 1);

        Assert.Equal(new byte[] { 128, 64, 128, 128, 128, 128 }, rgb.Where((_, i) => i % 3 == 0));
    }

    [Fact]
    public void AppliesWorldSpaceGroundBoundsOnlyToArtificialReceivers()
    {
        var camera = new ViewCropFrame(10, 20, 10, [1, 0, 0], [0, 1, 0], [0, 0, -1], -10, -10, 10, 10, false);
        var shadows = new ViewportShadows(camera, Vector3.UnitZ, 50, 3);
        shadows.AddCaster([[[8, 19, 5], [12, 19, 5], [12, 21, 5], [8, 21, 5]]], [0, 0, 1]);
        byte[] rgb = [255, 255, 255, 255, 255, 255, 128, 128, 128];

        shadows.Apply(rgb, [double.PositiveInfinity, double.PositiveInfinity, 7], [false, false, true],
            3, 1, -1.5, 0.5, 1, 1, 0, 0, 1, (x, y, z) => x == 10 && y == 20 && z == 3);

        Assert.Equal(new byte[] { 255, 128, 64 }, rgb.Where((_, i) => i % 3 == 0));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProjectsObliqueSunlightToTheOppositeSideOfTheCaster(bool groundOutsideModel)
    {
        var camera = new ViewCropFrame(0, 0, 10, [1, 0, 0], [0, 1, 0], [0, 0, -1], -10, -10, 10, 10, false);
        var shadows = new ViewportShadows(camera, new Vector3(1, 0, 1), 25, groundOutsideModel ? 0 : null);
        shadows.AddCaster([[[-0.75, -1, 2], [0.75, -1, 2], [0.75, 1, 2], [-0.75, 1, 2]]], [0, 0, 1]);
        byte baseColor = groundOutsideModel ? (byte)255 : (byte)80;
        byte shadowColor = groundOutsideModel ? (byte)191 : (byte)60;
        var rgb = Enumerable.Repeat(baseColor, 15).ToArray();
        var depth = Enumerable.Repeat(groundOutsideModel ? double.PositiveInfinity : 10d, 5).ToArray();
        var receivesLight = Enumerable.Repeat(!groundOutsideModel, 5).ToArray();

        shadows.Apply(rgb, depth, receivesLight, 5, 1, -4.5, 0.5, 1, 1, 0, 0, 1);

        Assert.Equal(new[] { baseColor, baseColor, shadowColor, baseColor, baseColor }, rgb.Where((_, i) => i % 3 == 0));
    }
}
