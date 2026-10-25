using DeveLiveStreamer.App.Studio;

namespace DeveLiveStreamer.Core.Tests;

public sealed class LayerGeometryTests
{
    [Fact]
    public void CoverCropsTheCenterWithoutDistortion()
    {
        // A 4:3 image in a 16:9 layer keeps full width and the middle 75% of the height.
        var map = LayerGeometry.Cover(4 / 3d, 16 / 9d);
        Assert.Equal((0, 0.125), Round(map.Map(0, 0)));
        Assert.Equal((1, 0.875), Round(map.Map(1, 1)));
        // A portrait camera in a landscape layer keeps its full width and shows a centered horizontal band.
        var tall = LayerGeometry.Cover(9 / 16d, 16 / 9d);
        var (_, top) = tall.Map(0, 0); var (_, bottom) = tall.Map(0, 1);
        Assert.Equal(9 / 16d / (16 / 9d), bottom - top, 9);
        Assert.Equal(0.5, (top + bottom) / 2, 9);
    }

    [Fact]
    public void ContainLetterboxesInsideTheLayer()
    {
        Assert.Equal((0, 0.125, 1, 0.75), Round4(LayerGeometry.Contain(16 / 9d, 4 / 3d)));
        Assert.Equal((0.25, 0, 0.5, 1), Round4(LayerGeometry.Contain(1, 2)));
    }

    [Theory]
    [InlineData(0, 0.5, 0.0)]   // natural orientation: the image's top is at the top
    [InlineData(90, 0.0, 0.5)]  // shown rotated counter-clockwise: the image's top is at the left
    [InlineData(180, 0.5, 1.0)]
    [InlineData(270, 1.0, 0.5)]
    public void DisplayRotationShowsTheNaturalImageCounterClockwise(int degrees, double screenX, double screenY)
    {
        // Sampling at the screen point must reach the top-center of the natural image.
        var (x, y) = LayerGeometry.DisplayRotation(degrees).Map(screenX, screenY);
        Assert.Equal((0.5, 0.0), Round((x, y)));
    }

    [Theory]
    [InlineData(1920, 1080, 90, 0, 1080 / 1920d)]
    [InlineData(1920, 1080, 90, 90, 1920 / 1080d)]
    [InlineData(1920, 1080, 0, 0, 1920 / 1080d)]
    [InlineData(1920, 1080, 270, 270, 1920 / 1080d)]
    public void CameraAspectFollowsSensorAndDisplayRotation(int width, int height, int sensor, int display, double expected) =>
        Assert.Equal(expected, LayerGeometry.CameraAspect(width, height, sensor, display), 9);

    [Fact]
    public void CameraSamplingStaysInsideTheTextureAndPreservesAspect()
    {
        foreach (var rotation in new[] { 0, 90, 180, 270 })
            foreach (var mirror in new[] { false, true })
            {
                var map = LayerGeometry.CameraSampling(9 / 16d, 16 / 9d, mirror, rotation);
                foreach (var (u, v) in new[] { (0d, 0d), (1d, 0d), (0d, 1d), (1d, 1d), (0.5, 0.5) })
                {
                    var (s, t) = map.Map(u, v);
                    Assert.InRange(s, -1e-9, 1 + 1e-9); Assert.InRange(t, -1e-9, 1 + 1e-9);
                }
                Assert.Equal((0.5, 0.5), Round(map.Map(0.5, 0.5)));
            }
        // Mirroring swaps left and right edges.
        var plain = LayerGeometry.CameraSampling(1, 1, false, 0).Map(0, 0.5);
        var mirrored = LayerGeometry.CameraSampling(1, 1, true, 0).Map(1, 0.5);
        Assert.Equal(Round(plain), Round(mirrored));
    }

    [Fact]
    public void GlMatricesAreColumnMajorAndComposeLikeTheAffineMaps()
    {
        var first = new Affine(0, -1, 1, 1, 0, 0);
        var second = LayerGeometry.FlipV;
        var product = LayerGeometry.Multiply(second.ToGl(), first.ToGl());
        var expected = second.After(first);
        Assert.Equal(expected.ToGl(), product);
        Assert.Equal((float)expected.C, product[12]);
    }

    private static (double, double) Round((double X, double Y) p) => (Math.Round(p.X, 9) + 0, Math.Round(p.Y, 9) + 0);
    private static (double, double, double, double) Round4((double, double, double, double) r) =>
        (Math.Round(r.Item1, 9), Math.Round(r.Item2, 9), Math.Round(r.Item3, 9), Math.Round(r.Item4, 9));
}
