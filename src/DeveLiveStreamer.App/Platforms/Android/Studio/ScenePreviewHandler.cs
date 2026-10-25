using Android.Graphics;
using Android.Views;
using DeveLiveStreamer.App.Controls;
using Microsoft.Maui.Handlers;

namespace DeveLiveStreamer.App.Platforms.Android.Studio;

/// <summary>Hosts the compositor's preview in a TextureView, so MAUI overlays (selection handles) can draw above it.</summary>
public sealed class ScenePreviewHandler() : ViewHandler<ScenePreview, TextureView>(ViewMapper)
{
    private Listener? listener;

    protected override TextureView CreatePlatformView()
    {
        var view = new TextureView(Context);
        view.SetOpaque(true);
        return view;
    }

    protected override void ConnectHandler(TextureView platformView)
    {
        base.ConnectHandler(platformView);
        listener = new Listener();
        platformView.SurfaceTextureListener = listener;
        if (platformView.IsAvailable && platformView.SurfaceTexture is { } texture)
            listener.OnSurfaceTextureAvailable(texture, platformView.Width, platformView.Height);
    }

    protected override void DisconnectHandler(TextureView platformView)
    {
        platformView.SurfaceTextureListener = null;
        listener?.Release();
        listener = null;
        base.DisconnectHandler(platformView);
    }

    private sealed class Listener : Java.Lang.Object, TextureView.ISurfaceTextureListener
    {
        private Surface? surface;

        public void OnSurfaceTextureAvailable(SurfaceTexture texture, int width, int height)
        {
            Release();
            surface = new Surface(texture);
            AndroidStudioEngine.Instance.AttachPreview(surface, width, height);
        }

        public void OnSurfaceTextureSizeChanged(SurfaceTexture texture, int width, int height) => AndroidStudioEngine.Instance.ResizePreview(width, height);

        public bool OnSurfaceTextureDestroyed(SurfaceTexture texture)
        {
            Release();
            return true;
        }

        public void OnSurfaceTextureUpdated(SurfaceTexture texture) { }

        public void Release()
        {
            if (surface is null) return;
            // Synchronous: the compositor must stop drawing before the surface goes away.
            try { AndroidStudioEngine.Instance.DetachPreview(); }
            catch (TimeoutException ex) { global::Android.Util.Log.Warn("ScenePreview", ex.Message); }
            surface.Release();
            surface = null;
        }
    }
}
