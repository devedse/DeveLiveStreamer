using Android.Hardware.Display;
using Android.Media.Projection;
using Android.OS;

namespace DeveLiveStreamer.App.Platforms.Android.Studio;

/// <summary>System-wide screen capture through MediaProjection into an <see cref="ExternalTexture"/>.</summary>
internal sealed class ScreenSource : IDisposable
{
    private VirtualDisplay? display;
    private int densityDpi;

    public ScreenSource(MediaProjection projection, int width, int height, int densityDpi, Handler handler)
    {
        this.densityDpi = densityDpi;
        Texture = new ExternalTexture(width, height, handler);
        // The live session owns the projection callback and registers it before creating this display.
        try
        {
            display = projection.CreateVirtualDisplay("DeveLiveStreamer", width, height, densityDpi,
                (global::Android.Views.DisplayFlags)VirtualDisplayFlags.AutoMirror, Texture.Surface, null, handler);
        }
        catch { Texture.Dispose(); throw; }
    }

    public ExternalTexture Texture { get; }
    public double Aspect => (double)Texture.Width / Texture.Height;

    /// <summary>Follows device rotation so the captured screen keeps its shape.</summary>
    public void Resize(int width, int height, int dpi)
    {
        if (display is null || (width == Texture.Width && height == Texture.Height)) return;
        densityDpi = dpi;
        Texture.Resize(width, height);
        display.Resize(width, height, densityDpi);
    }

    public void Dispose()
    {
        try { display?.Release(); } catch { }
        display = null;
        Texture.Dispose();
    }

}
