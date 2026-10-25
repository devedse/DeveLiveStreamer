using System.Collections.Concurrent;
using System.Diagnostics;
using Android.Content;
using Android.Graphics;
using Android.Hardware.Camera2;
using Android.Media.Projection;
using Android.Opengl;
using Android.OS;
using Android.Text;
using DeveLiveStreamer.App.Studio;
using DeveLiveStreamer.Core;
using DeveLiveStreamer.Streaming.Audio;
using AndroidColor = Android.Graphics.Color;
using AndroidSurface = Android.Views.Surface;
using TextLayout = Android.Text.Layout;

namespace DeveLiveStreamer.App.Platforms.Android.Studio;

/// <summary>
/// Renders the program scene on a dedicated GL thread. Each tick draws all layers into an output-sized framebuffer,
/// which then feeds the encoder surface (at the output frame rate, independent of the display) and the on-screen preview.
/// </summary>
internal sealed class Compositor : IDisposable
{
    private const long PreviewPeriodNs = 1_000_000_000 / 30;
    private const long PreviewIntervalNs = 1_000_000_000 / 60;
    private static readonly float[] FlipV = LayerGeometry.FlipV.ToGl();
    private readonly Context context;
    private readonly Handler callbacks;
    private readonly Thread thread;
    private readonly ConcurrentQueue<Action> commands = new();
    private readonly AutoResetEvent wake = new(false);
    private readonly GlRenderer gl = new();
    private readonly Dictionary<string, CameraSource> cameras = [];
    private readonly Dictionary<string, long> lastUsed = [];
    private readonly Dictionary<string, NetworkSource> networks = [];
    private readonly Dictionary<string, BitmapTexture> bitmaps = [];
    private readonly Dictionary<(string, bool), string?> cameraIds = [];
    private readonly Dictionary<string, string> statuses = [];
    private volatile RenderScene scene = RenderScene.Empty;
    private volatile bool running = true;
    private (AndroidSurface Surface, EGLSurface Egl, int Width, int Height)? preview;
    private (EGLSurface Egl, int Width, int Height, long PeriodNs)? encoder;
    private GlRenderer.Framebuffer? framebuffer;
    private ScreenSource? screen;
    private Typeface? typeface;
    private int outputWidth = 1920, outputHeight = 1080;
    private double outputFps = 30;
    private long renderedThisSecond, secondStart;
    private int cameraCount;
    // Offline, the preview is only redrawn when something visible may have changed.
    private volatile bool dirty = true;
    private CameraManager? cameraManager;

    public Compositor(Context context, Handler callbacks)
    {
        this.context = context; this.callbacks = callbacks;
        thread = new Thread(Run) { IsBackground = true, Name = "Compositor", Priority = System.Threading.ThreadPriority.AboveNormal };
        thread.Start();
    }

    public volatile int DisplayRotation;
    public volatile bool CameraPermitted;
    public Func<AudioMixer?> Mixer { get; set; } = () => null;
    public string? FatalError { get; private set; }
    public double RenderFps { get; private set; }
    public long DroppedFrames;
    /// <summary>Frames handed to the encoder surface since encoding started.</summary>
    public long FramesToEncoder;
    private long encoderSwapFailures;
    public event Action? SourcesChanged;

    public void UpdateScene(RenderScene value) { scene = value; dirty = true; }

    public void SetOutput(int width, int height, double fps) => Post(() => ApplyOutput(width, height, fps));

    private void ApplyOutput(int width, int height, double fps)
    {
        if (width == outputWidth && height == outputHeight && Math.Abs(fps - outputFps) < 0.001) return;
        (outputWidth, outputHeight, outputFps) = (width, height, fps);
        // Cameras are opened for the output's size and rate; reopen them with the new request.
        foreach (var camera in cameras.Values) camera.Dispose();
        cameras.Clear();
    }

    public string? SourceStatus(string sourceId) { lock (statuses) return statuses.GetValueOrDefault(sourceId); }

    public void AttachPreview(AndroidSurface surface, int width, int height) => Post(() =>
    {
        DetachPreviewNow();
        var egl = gl.CreateWindowSurface(surface);
        gl.MakeCurrent(egl);
        gl.SetSwapInterval(0); // never let display vsync pace the output
        preview = (surface, egl, width, height);
        dirty = true;
    });

    public void ResizePreview(int width, int height) => Post(() => { if (preview is { } p) preview = p with { Width = width, Height = height }; dirty = true; });
    public void DetachPreview() => Invoke(DetachPreviewNow);

    private void DetachPreviewNow()
    {
        if (preview is not { } p) return;
        gl.DestroySurface(p.Egl);
        preview = null;
    }

    public void StartEncoding(AndroidSurface surface, int width, int height, double fps) => Invoke(() =>
    {
        if (FatalError is not null) throw new InvalidOperationException(FatalError);
        var egl = gl.CreateWindowSurface(surface);
        encoder = (egl, width, height, (long)Math.Round(1_000_000_000 / fps));
        FramesToEncoder = encoderSwapFailures = 0;
        ApplyOutput(width, height, fps);
        try { ReplaceFramebuffer(width, height); }
        catch { gl.DestroySurface(egl); encoder = null; throw; }
    });

    public void StopEncoding() => Invoke(() =>
    {
        if (encoder is not { } e) return;
        gl.DestroySurface(e.Egl);
        encoder = null;
    });

    public void StartScreenCapture(MediaProjection projection, int width, int height, int dpi) => Invoke(() =>
    {
        screen?.Dispose();
        screen = new ScreenSource(projection, width, height, dpi, callbacks);
    });

    public void ResizeScreenCapture(int width, int height, int dpi) => Post(() => screen?.Resize(width, height, dpi));
    public void StopScreenCapture() => Invoke(() => { screen?.Dispose(); screen = null; });

    private void Post(Action action) { commands.Enqueue(action); wake.Set(); }

    private void Invoke(Action action)
    {
        if (Thread.CurrentThread == thread) { action(); return; }
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() => { try { action(); done.SetResult(); } catch (Exception ex) { done.SetException(ex); } });
        if (!done.Task.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("The compositor did not respond.");
    }

    private static long Now() => Stopwatch.GetTimestamp() * (1_000_000_000 / Stopwatch.Frequency);

    private void Run()
    {
        try
        {
            gl.Initialize();
            typeface = Typeface.CreateFromAsset(context.Assets, "OpenSans-Semibold.ttf");
        }
        catch (Exception ex) { FatalError = "The GPU compositor could not start: " + ex.Message; }
        var next = Now();
        secondStart = next;
        while (running)
        {
            while (commands.TryDequeue(out var command))
            {
                try { command(); } catch (Exception ex) { global::Android.Util.Log.Warn("Compositor", ex.ToString()); }
            }
            if (FatalError is not null || (preview is null && encoder is null))
            {
                ReleaseIdleSources(all: true);
                wake.WaitOne(250);
                next = Now();
                continue;
            }
            var period = encoder?.PeriodNs ?? PreviewPeriodNs;
            var now = Now();
            if (now < next)
            {
                var wait = (next - now) / 1_000_000;
                if (wait >= 1) wake.WaitOne((int)Math.Min(wait, 50)); else Thread.Yield();
                continue;
            }
            var frameTime = next;
            next += period;
            if (now - frameTime > 2 * period)
            {
                // Fell behind (GPU or encoder back-pressure): skip the missed ticks instead of bursting.
                Interlocked.Add(ref DroppedFrames, (now - frameTime) / period);
                frameTime = now; next = now + period;
            }
            try { Render(frameTime); }
            catch (Exception ex) { global::Android.Util.Log.Warn("Compositor", ex.ToString()); wake.WaitOne(100); }
        }
        foreach (var camera in cameras.Values) camera.Dispose();
        foreach (var network in networks.Values) { network.Dispose(); network.Texture.Dispose(); }
        foreach (var bitmap in bitmaps.Values) bitmap.Dispose();
        screen?.Dispose();
        DetachPreviewNow();
        if (encoder is { } e) gl.DestroySurface(e.Egl);
        framebuffer?.Dispose();
        gl.Dispose();
    }

    private long lastPreview;

    private void Render(long frameTime)
    {
        var current = scene;
        if (SyncSources(current)) dirty = true;
        foreach (var camera in cameras.Values) if (camera.Texture.Update()) dirty = true;
        foreach (var network in networks.Values) if (network.Texture.Update()) dirty = true;
        if (screen?.Texture.Update() == true) dirty = true;
        if (encoder is null && !dirty) return;
        dirty = false;

        gl.MakeCurrent(encoder?.Egl ?? preview!.Value.Egl);
        if (encoder is null) EnsurePreviewFramebuffer();
        var target = framebuffer!;
        GlRenderer.BindTarget(target, target.Width, target.Height);
        GlRenderer.Clear(0, 0, 0);
        // Placeholders for sources without video are drawn in layer order, but only when nothing is being encoded:
        // while live, the preview is exactly the streamed frame.
        var placeholders = encoder is null;
        foreach (var layer in current.Layers) DrawLayer(layer, target.Width, target.Height, placeholders);

        if (encoder is { } e)
        {
            gl.MakeCurrent(e.Egl);
            GlRenderer.BindTarget(null, e.Width, e.Height);
            gl.DrawTexture(target.Texture, false, (0, 0, 1, 1), FlipV, 1);
            gl.SetPresentationTime(e.Egl, frameTime);
            if (gl.Swap(e.Egl)) Interlocked.Increment(ref FramesToEncoder);
            else if (Interlocked.Increment(ref encoderSwapFailures) <= 3)
                global::Android.Util.Log.Warn("Compositor", $"Encoder eglSwapBuffers failed (0x{EGL14.EglGetError():X})");
        }
        if (preview is { } p && frameTime - lastPreview >= PreviewIntervalNs)
        {
            lastPreview = frameTime;
            gl.MakeCurrent(p.Egl);
            GlRenderer.BindTarget(null, p.Width, p.Height);
            GlRenderer.Clear(0.04f, 0.06f, 0.08f);
            var (x, y, w, h) = LayerGeometry.Contain((double)target.Width / target.Height, (double)p.Width / p.Height);
            gl.DrawTexture(target.Texture, false, (x, y, w, h), FlipV, 1);
            gl.Swap(p.Egl);
        }
        renderedThisSecond++;
        if (frameTime - secondStart >= 1_000_000_000)
        {
            RenderFps = renderedThisSecond * 1e9 / (frameTime - secondStart);
            renderedThisSecond = 0; secondStart = frameTime;
        }
    }

    private void EnsurePreviewFramebuffer()
    {
        // Without an encoder, render at a size that fits the preview to save power; text stays proportional.
        var (_, _, pw, ph) = preview!.Value;
        var scale = Math.Min(1, Math.Max((double)pw / outputWidth, (double)ph / outputHeight));
        int width = Math.Max(2, (int)(outputWidth * scale)), height = Math.Max(2, (int)(outputHeight * scale));
        if (framebuffer is null || framebuffer.Width != width || framebuffer.Height != height) ReplaceFramebuffer(width, height);
    }

    private void ReplaceFramebuffer(int width, int height)
    {
        var limit = gl.MaxTextureSize;
        if (limit > 0 && (width > limit || height > limit))
            throw new InvalidOperationException($"This GPU renders frames up to {limit}×{limit}; {width}×{height} is larger.");
        framebuffer?.Dispose();
        framebuffer = GlRenderer.CreateFramebuffer(width, height);
    }

    private void DrawLayer(RenderLayer layer, int width, int height, bool placeholders)
    {
        var rect = (layer.X, layer.Y, layer.Width, layer.Height);
        var alpha = (float)layer.Opacity;
        var pixelWidth = layer.Width * width; var pixelHeight = layer.Height * height;
        if (pixelWidth < 1 || pixelHeight < 1) return;
        var layerAspect = pixelWidth / pixelHeight;
        switch (layer.Kind)
        {
            case SourceKind.Color:
                var color = ParseColor(layer.Color);
                gl.DrawColor(rect, color.R / 255f, color.G / 255f, color.B / 255f, color.A / 255f * alpha);
                break;
            case SourceKind.Text:
                var text = TextTexture(layer, (int)Math.Ceiling(pixelWidth), (int)Math.Ceiling(pixelHeight), height);
                gl.DrawTexture(text.Id, false, rect, (layer.Mirror ? LayerGeometry.MirrorX : Affine.Identity).ToGl(), alpha);
                break;
            case SourceKind.Image:
                if (ImageTexture(layer.Content, width, height) is { } image)
                    gl.DrawTexture(image.Id, false, Inside(rect, LayerGeometry.Contain(image.Aspect, layerAspect)),
                        (layer.Mirror ? LayerGeometry.MirrorX : Affine.Identity).ToGl(), alpha);
                break;
            case SourceKind.Camera:
                if (CameraFor(layer) is { Texture.HasFrame: true } camera)
                {
                    var rotation = DisplayRotation;
                    var aspect = LayerGeometry.CameraAspect(camera.Texture.Width, camera.Texture.Height, camera.SensorOrientation, rotation);
                    var sampling = LayerGeometry.CameraSampling(aspect, layerAspect, layer.Mirror, rotation);
                    gl.DrawTexture(camera.Texture.Id, true, rect, LayerGeometry.Multiply(camera.Texture.Matrix, sampling.ToGl()), alpha);
                }
                else if (placeholders) DrawPlaceholder(layer, "CAMERA", (int)pixelWidth, (int)pixelHeight);
                break;
            case SourceKind.Screen:
                if (screen is { Texture.HasFrame: true } capture) DrawExternal(capture.Texture, capture.Aspect, layer, rect, layerAspect, alpha);
                else if (placeholders) DrawPlaceholder(layer, "DEVICE SCREEN", (int)pixelWidth, (int)pixelHeight);
                break;
            case SourceKind.Network:
                if (networks.GetValueOrDefault(NetworkKey(layer)) is { Texture.HasFrame: true } network)
                    DrawExternal(network.Texture, network.Aspect, layer, rect, layerAspect, alpha);
                else if (placeholders) DrawPlaceholder(layer, "NETWORK STREAM", (int)pixelWidth, (int)pixelHeight);
                break;
        }
    }

    private void DrawExternal(ExternalTexture texture, double aspect, RenderLayer layer, (double, double, double, double) rect, double layerAspect, float alpha)
    {
        var sampling = layer.Mirror ? LayerGeometry.FlipV.After(LayerGeometry.MirrorX) : LayerGeometry.FlipV;
        gl.DrawTexture(texture.Id, true, Inside(rect, LayerGeometry.Contain(aspect, layerAspect)), LayerGeometry.Multiply(texture.Matrix, sampling.ToGl()), alpha);
    }

    private static (double X, double Y, double W, double H) Inside((double X, double Y, double W, double H) outer, (double X, double Y, double Width, double Height) inner) =>
        (outer.X + inner.X * outer.W, outer.Y + inner.Y * outer.H, inner.Width * outer.W, inner.Height * outer.H);

    /// <summary>A labelled box for a source that has nothing to show yet; drawn only while not encoding.</summary>
    private void DrawPlaceholder(RenderLayer layer, string title, int pixelWidth, int pixelHeight)
    {
        var rect = (layer.X, layer.Y, layer.Width, layer.Height);
        gl.DrawColor(rect, 0.075f, 0.137f, 0.176f, (float)layer.Opacity);
        if (pixelWidth < 8 || pixelHeight < 8) return;
        var status = SourceStatus(layer.SourceId) ?? (layer.Kind == SourceKind.Screen ? "Screen capture starts when you go live" : "Waiting for video…");
        // Size relative to the frame so the label fits at any output resolution.
        var label = CachedTexture("placeholder:" + layer.ItemId, $"{title}|{status}|{pixelWidth}x{pixelHeight}", () =>
            RenderText($"{title}\n\n{status}", AndroidColor.ParseColor("#93A4B5"), Math.Max(4f, Math.Min(pixelHeight / 14f, pixelWidth / 30f)),
                pixelWidth, pixelHeight, TextLayout.Alignment.AlignCenter!, padding: 0.06f));
        gl.DrawTexture(label.Id, false, rect, GlRenderer.IdentityMatrix, (float)layer.Opacity);
    }

    private BitmapTexture TextTexture(RenderLayer layer, int width, int height, int frameHeight)
    {
        width = Math.Clamp(width, 1, Math.Max(1, gl.MaxTextureSize)); height = Math.Clamp(height, 1, Math.Max(1, gl.MaxTextureSize));
        // Font size is specified in canvas pixels; scale it to the framebuffer.
        var fontPixels = (float)(layer.FontSize * frameHeight / Math.Max(1, scene.CanvasHeight));
        return CachedTexture("text:" + layer.ItemId, $"{layer.Content}|{layer.Color}|{fontPixels}|{width}x{height}", () =>
            RenderText(layer.Content, ParseColor(layer.Color), fontPixels, width, height, TextLayout.Alignment.AlignNormal!, padding: 0));
    }

    private Bitmap RenderText(string text, AndroidColor color, float size, int width, int height, TextLayout.Alignment alignment, float padding)
    {
        var bitmap = global::Android.Graphics.Bitmap.CreateBitmap(width, height, global::Android.Graphics.Bitmap.Config.Argb8888!)!;
        using var canvas = new Canvas(bitmap);
        using var paint = new TextPaint(PaintFlags.AntiAlias) { Color = color, TextSize = Math.Max(1, size) };
        paint.SetTypeface(typeface ?? Typeface.DefaultBold);
        var inset = (int)(width * padding);
        var available = Math.Max(1, width - inset * 2);
        using var layout = StaticLayout.Builder.Obtain(text, 0, text.Length, paint, available).SetAlignment(alignment).SetIncludePad(false).Build();
        canvas.Translate(inset, (height - layout.Height) / 2f);
        layout.Draw(canvas);
        return bitmap;
    }

    private BitmapTexture? ImageTexture(string path, int frameWidth, int frameHeight)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        try
        {
            return CachedTexture("image:" + path, $"{File.GetLastWriteTimeUtc(path).Ticks}|{frameWidth}x{frameHeight}", () =>
            {
                using var bounds = new BitmapFactory.Options { InJustDecodeBounds = true };
                BitmapFactory.DecodeFile(path, bounds);
                var sample = 1;
                var limit = Math.Max(frameWidth, frameHeight) * 2;
                while (Math.Max(bounds.OutWidth, bounds.OutHeight) / (sample * 2) >= limit) sample *= 2;
                using var options = new BitmapFactory.Options { InSampleSize = sample, InPreferredConfig = global::Android.Graphics.Bitmap.Config.Argb8888 };
                return BitmapFactory.DecodeFile(path, options) ?? throw new InvalidDataException("The image could not be decoded.");
            });
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or Java.Lang.Exception) { return null; }
    }

    /// <summary>Returns a cached texture, re-rasterizing when the content key changes.</summary>
    private BitmapTexture CachedTexture(string key, string content, Func<Bitmap> create)
    {
        if (bitmaps.TryGetValue(key, out var cached) && cached.Content == content) { cached.LastUsed = Now(); return cached; }
        cached?.Dispose();
        using var bitmap = create();
        var id = GlRenderer.CreateTexture(external: false);
        GlRenderer.Upload(id, bitmap);
        var texture = new BitmapTexture(id, content, (double)bitmap.Width / bitmap.Height) { LastUsed = Now() };
        bitmap.Recycle();
        bitmaps[key] = texture;
        return texture;
    }

    private static AndroidColor ParseColor(string value)
    {
        try { return AndroidColor.ParseColor(value); } catch { return AndroidColor.White; }
    }

    private static string NetworkKey(RenderLayer layer) => layer.SourceId + "|" + layer.Content.Trim();

    private CameraSource? CameraFor(RenderLayer layer)
    {
        if (!cameraIds.TryGetValue((layer.CameraId, layer.FrontCamera), out var id)) return null;
        return id is null ? null : cameras.GetValueOrDefault(id);
    }

    /// <summary>Opens what the program scene needs and closes what it no longer uses (after a short grace period).</summary>
    private bool SyncSources(RenderScene current)
    {
        var now = Now();
        var manager = cameraManager ??= (CameraManager)context.GetSystemService(Context.CameraService)!;
        var changed = false;
        var seen = new HashSet<string>();
        foreach (var layer in current.Layers)
        {
            string? status = null;
            switch (layer.Kind)
            {
                case SourceKind.Camera:
                    if (!cameraIds.TryGetValue((layer.CameraId, layer.FrontCamera), out var id))
                    {
                        try { id = CameraSource.Resolve(manager, layer.CameraId, layer.FrontCamera); } catch (Exception) { id = null; }
                        cameraIds[(layer.CameraId, layer.FrontCamera)] = id;
                    }
                    if (id is null) { status = "This camera is not available on this device"; break; }
                    if (!CameraPermitted) { status = "Allow camera access to show this camera"; break; }
                    lastUsed["camera:" + id] = now;
                    if (!cameras.TryGetValue(id, out var camera))
                    {
                        try { cameras[id] = camera = CameraSource.Open(context, id, outputWidth, outputHeight, outputFps, callbacks); }
                        catch (Exception ex) { status = "Camera unavailable: " + ex.Message; break; }
                    }
                    status = camera.Status;
                    break;
                case SourceKind.Network:
                    if (string.IsNullOrWhiteSpace(layer.Content)) { status = "Set an incoming RTMP or SRT URL in Properties"; break; }
                    var key = NetworkKey(layer);
                    lastUsed["network:" + key] = now;
                    if (!networks.TryGetValue(key, out var network))
                    {
                        try { networks[key] = network = new NetworkSource(layer.Content.Trim(), layer.AudioEnabled, () => Mixer(), callbacks); }
                        catch (Exception ex) { status = ex.Message; break; }
                    }
                    network.AudioEnabled = layer.AudioEnabled;
                    status = network.Status;
                    break;
                case SourceKind.Screen:
                    status = screen is null ? "Screen capture needs permission; while live, switch away and back to request it" : screen.Texture.HasFrame ? null : "Waiting for the screen…";
                    break;
                default: continue;
            }
            if (!seen.Add(layer.SourceId)) continue;
            lock (statuses)
            {
                if (statuses.GetValueOrDefault(layer.SourceId) != status)
                {
                    if (status is null) statuses.Remove(layer.SourceId); else statuses[layer.SourceId] = status;
                    changed = true;
                }
            }
        }
        lock (statuses)
            foreach (var stale in statuses.Keys.Where(k => !seen.Contains(k)).ToArray()) { statuses.Remove(stale); changed = true; }
        ReleaseIdleSources(all: false);
        if (changed || cameras.Count != cameraCount)
        {
            cameraCount = cameras.Count;
            CameraSummary = string.Join("; ", cameras.Values.Select(c => $"camera {c.CameraId}: {c.Describe()}"));
        }
        if (changed) SourcesChanged?.Invoke();
        return changed;
    }

    private void ReleaseIdleSources(bool all)
    {
        var now = Now();
        foreach (var (id, camera) in cameras.ToArray())
            if (all || now - lastUsed.GetValueOrDefault("camera:" + id) > 1_500_000_000L) { camera.Dispose(); cameras.Remove(id); }
        foreach (var (key, network) in networks.ToArray())
            if (all || now - lastUsed.GetValueOrDefault("network:" + key) > 5_000_000_000L)
            {
                network.Dispose(); network.Texture.Dispose(); networks.Remove(key);
            }
        foreach (var (key, bitmap) in bitmaps.ToArray())
            if (all || now - bitmap.LastUsed > 10_000_000_000L) { bitmap.Dispose(); bitmaps.Remove(key); }
        if (all) cameraIds.Clear();
    }

    /// <summary>Actual capture configuration of the open cameras, for diagnostics.</summary>
    public volatile string CameraSummary = "";

    public void Dispose()
    {
        running = false;
        wake.Set();
        thread.Join(3000);
    }

    private sealed class BitmapTexture(int id, string content, double aspect) : IDisposable
    {
        public int Id { get; } = id;
        public string Content { get; } = content;
        public double Aspect { get; } = aspect;
        public long LastUsed { get; set; }
        public void Dispose() => GlRenderer.DeleteTexture(Id);
    }
}
