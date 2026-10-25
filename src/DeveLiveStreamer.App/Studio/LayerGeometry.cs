namespace DeveLiveStreamer.App.Studio;

/// <summary>A 2D affine map: x' = A·x + B·y + C, y' = D·x + E·y + F.</summary>
public readonly record struct Affine(double A, double B, double C, double D, double E, double F)
{
    public static readonly Affine Identity = new(1, 0, 0, 0, 1, 0);
    public (double X, double Y) Map(double x, double y) => (A * x + B * y + C, D * x + E * y + F);
    /// <summary>The map that applies <paramref name="first"/> and then this one.</summary>
    public Affine After(Affine first) => new(
        A * first.A + B * first.D, A * first.B + B * first.E, A * first.C + B * first.F + C,
        D * first.A + E * first.D, D * first.B + E * first.E, D * first.C + E * first.F + F);
    /// <summary>Column-major 4×4 matrix for a GLSL mat4 applied to (x, y, 0, 1).</summary>
    public float[] ToGl() => [(float)A, (float)D, 0, 0, (float)B, (float)E, 0, 0, 0, 0, 1, 0, (float)C, (float)F, 0, 1];
}

/// <summary>
/// Texture-coordinate mapping for scene layers. Layer quads use image space: (0,0) top-left, (1,1) bottom-right.
/// </summary>
public static class LayerGeometry
{
    /// <summary>Samples the centered part of an image that fills the target without distortion (crop).</summary>
    public static Affine Cover(double imageAspect, double targetAspect)
    {
        if (!(imageAspect > 0) || !(targetAspect > 0)) return Affine.Identity;
        if (imageAspect > targetAspect) { var w = targetAspect / imageAspect; return new(w, 0, (1 - w) / 2, 0, 1, 0); }
        var h = imageAspect / targetAspect;
        return new(1, 0, 0, 0, h, (1 - h) / 2);
    }

    /// <summary>The part of a layer (normalized) in which an image fits completely (letterbox/pillarbox).</summary>
    public static (double X, double Y, double Width, double Height) Contain(double imageAspect, double targetAspect)
    {
        if (!(imageAspect > 0) || !(targetAspect > 0)) return (0, 0, 1, 1);
        if (imageAspect > targetAspect) { var h = targetAspect / imageAspect; return (0, (1 - h) / 2, 1, h); }
        var w = imageAspect / targetAspect;
        return ((1 - w) / 2, 0, w, 1);
    }

    public static readonly Affine MirrorX = new(-1, 0, 1, 0, 1, 0);
    /// <summary>Image space (v down) to OpenGL texture space (t up).</summary>
    public static readonly Affine FlipV = new(1, 0, 0, 0, -1, 1);

    /// <summary>
    /// Maps upright on-screen coordinates into a camera image that is upright for the device's natural orientation.
    /// That image must appear rotated counter-clockwise by the display rotation, so sampling rotates clockwise.
    /// </summary>
    public static Affine DisplayRotation(int degrees) => (((degrees % 360) + 360) % 360) switch
    {
        90 => new(0, -1, 1, 1, 0, 0),
        180 => new(-1, 0, 1, 0, -1, 1),
        270 => new(0, 1, 0, -1, 0, 1),
        _ => Affine.Identity
    };

    /// <summary>
    /// Aspect ratio of the camera image as displayed: Android's SurfaceTexture transform already rotates the sensor buffer
    /// to the natural orientation; the display rotation may swap the axes again.
    /// </summary>
    public static double CameraAspect(int bufferWidth, int bufferHeight, int sensorOrientation, int displayRotation)
    {
        var natural = sensorOrientation % 180 != 0 ? (double)bufferHeight / bufferWidth : (double)bufferWidth / bufferHeight;
        return displayRotation % 180 != 0 ? 1 / natural : natural;
    }

    /// <summary>Layer image space → camera texture space before the SurfaceTexture transform: crop, mirror, rotate, flip.</summary>
    public static Affine CameraSampling(double displayedAspect, double layerAspect, bool mirror, int displayRotation)
    {
        var map = Cover(displayedAspect, layerAspect);
        if (mirror) map = MirrorX.After(map);
        return FlipV.After(DisplayRotation(displayRotation).After(map));
    }

    /// <summary>4×4 column-major product a·b, as used for combining with SurfaceTexture.getTransformMatrix.</summary>
    public static float[] Multiply(float[] a, float[] b)
    {
        var result = new float[16];
        for (var column = 0; column < 4; column++)
            for (var row = 0; row < 4; row++)
            {
                float sum = 0;
                for (var k = 0; k < 4; k++) sum += a[k * 4 + row] * b[column * 4 + k];
                result[column * 4 + row] = sum;
            }
        return result;
    }
}
