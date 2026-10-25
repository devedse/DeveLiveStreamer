using Android.Graphics;
using Android.OS;
using AndroidSurface = Android.Views.Surface;

namespace DeveLiveStreamer.App.Platforms.Android.Studio;

/// <summary>
/// An OES texture fed by a producer (camera, virtual display, decoder) through a SurfaceTexture.
/// Created, updated and disposed on the compositor thread.
/// </summary>
internal sealed class ExternalTexture : Java.Lang.Object, SurfaceTexture.IOnFrameAvailableListener
{
    private int pending;

    public ExternalTexture(int width, int height, Handler callbacks)
    {
        Id = GlRenderer.CreateTexture(external: true);
        SurfaceTexture = new SurfaceTexture(Id);
        SurfaceTexture.SetDefaultBufferSize(width, height);
        SurfaceTexture.SetOnFrameAvailableListener(this, callbacks);
        Surface = new AndroidSurface(SurfaceTexture);
        Width = width; Height = height;
    }

    public int Id { get; }
    public SurfaceTexture SurfaceTexture { get; }
    public AndroidSurface Surface { get; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public float[] Matrix { get; } = new float[16];
    public bool HasFrame { get; private set; }
    public long Frames { get; private set; }

    public void Resize(int width, int height)
    {
        SurfaceTexture.SetDefaultBufferSize(width, height);
        Width = width; Height = height;
    }

    public void OnFrameAvailable(SurfaceTexture? surfaceTexture) => Interlocked.Increment(ref pending);

    /// <summary>Latches the newest frame; returns whether a new frame arrived since the last call.</summary>
    public bool Update()
    {
        var count = Interlocked.Exchange(ref pending, 0);
        if (count == 0) return false;
        // Each call consumes one queued buffer; catching up keeps latency at one frame.
        for (var i = 0; i < Math.Min(count, 4); i++) SurfaceTexture.UpdateTexImage();
        SurfaceTexture.GetTransformMatrix(Matrix);
        HasFrame = true;
        Frames++;
        return true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SurfaceTexture.SetOnFrameAvailableListener(null);
            Surface.Release();
            SurfaceTexture.Release();
            GlRenderer.DeleteTexture(Id);
        }
        base.Dispose(disposing);
    }
}
