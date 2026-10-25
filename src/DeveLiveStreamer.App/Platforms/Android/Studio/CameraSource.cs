using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.Hardware.Camera2;
using Android.Hardware.Camera2.Params;
using Android.OS;
using AndroidSize = Android.Util.Size;
using AndroidRange = Android.Util.Range;

namespace DeveLiveStreamer.App.Platforms.Android.Studio;

/// <summary>
/// One opened camera device rendering into an <see cref="ExternalTexture"/>. Several scene layers that use the same
/// camera share one instance. Uses a constrained high-speed session when the requested rate needs it.
/// </summary>
internal sealed class CameraSource : IDisposable
{
    private readonly Handler handler;
    private readonly double fps;
    private CameraDevice? device;
    private CameraCaptureSession? session;
    private bool disposed;

    private CameraSource(string id, ExternalTexture texture, int sensorOrientation, double fps, bool highSpeed, AndroidRange? range, Handler handler)
    {
        CameraId = id; Texture = texture; SensorOrientation = sensorOrientation; this.fps = fps;
        HighSpeed = highSpeed; FpsRange = range; this.handler = handler;
    }

    public string CameraId { get; }
    public ExternalTexture Texture { get; }
    public int SensorOrientation { get; }
    public bool HighSpeed { get; }
    public AndroidRange? FpsRange { get; }
    public volatile string? Status = "Opening camera…";

    /// <summary>Resolves a layer's camera choice to a device ID: the explicit ID if present, otherwise the first camera facing the requested way.</summary>
    public static string? Resolve(CameraManager manager, string requestedId, bool front)
    {
        var ids = manager.GetCameraIdList();
        if (!string.IsNullOrEmpty(requestedId)) return ids.Contains(requestedId) ? requestedId : null;
        var facing = front ? (int)LensFacing.Front : (int)LensFacing.Back;
        return ids.FirstOrDefault(id => (manager.GetCameraCharacteristics(id).Get(CameraCharacteristics.LensFacing) as Java.Lang.Integer)?.IntValue() == facing)
            ?? ids.FirstOrDefault();
    }

    public static CameraSource Open(Context context, string cameraId, int outputWidth, int outputHeight, double fps, Handler handler)
    {
        var manager = (CameraManager)context.GetSystemService(Context.CameraService)!;
        var characteristics = manager.GetCameraCharacteristics(cameraId);
        var orientation = (characteristics.Get(CameraCharacteristics.SensorOrientation) as Java.Lang.Integer)?.IntValue() ?? 0;
        var map = characteristics.Get(CameraCharacteristics.ScalerStreamConfigurationMap) as StreamConfigurationMap
            ?? throw new InvalidOperationException("The camera reports no output sizes.");
        // Sensors are landscape; compare against the landscape form of the output.
        int targetWidth = Math.Max(outputWidth, outputHeight), targetHeight = Math.Min(outputWidth, outputHeight);
        var ranges = JavaArray<AndroidRange>(characteristics.Get(CameraCharacteristics.ControlAeAvailableTargetFpsRanges));
        var regularMaximum = ranges.Select(Upper).DefaultIfEmpty(30).Max();
        AndroidSize size; AndroidRange? range; var highSpeed = false;
        if (fps > regularMaximum + 0.01 && HighSpeedChoice(map, targetWidth, targetHeight, fps) is { } choice)
        {
            (size, range, highSpeed) = (choice.Size, choice.Range, true);
        }
        else
        {
            size = ChooseSize(map.GetOutputSizes(Java.Lang.Class.FromType(typeof(SurfaceTexture))) ?? [], targetWidth, targetHeight);
            range = ranges.Where(r => Upper(r) >= Math.Ceiling(fps) - 0.01).OrderBy(r => Upper(r)).ThenByDescending(Lower).FirstOrDefault()
                ?? ranges.OrderByDescending(Upper).ThenByDescending(Lower).FirstOrDefault();
        }
        var texture = new ExternalTexture(size.Width, size.Height, handler);
        var source = new CameraSource(cameraId, texture, orientation, fps, highSpeed, range, handler);
        if (context.CheckSelfPermission(global::Android.Manifest.Permission.Camera) != Permission.Granted)
        {
            source.Status = "Camera access is not allowed";
            return source;
        }
        try { manager.OpenCamera(cameraId, new DeviceCallback(source), handler); }
        catch (Exception ex) { source.Status = "Camera unavailable: " + ex.Message; }
        return source;
    }

    internal static T[] JavaArray<T>(Java.Lang.Object? value) where T : Java.Lang.Object =>
        value is null ? [] : global::Android.Runtime.JNIEnv.GetArray<T>(value.Handle) ?? [];

    private static int Upper(AndroidRange range) => ((Java.Lang.Integer)range.Upper!).IntValue();
    private static int Lower(AndroidRange range) => ((Java.Lang.Integer)range.Lower!).IntValue();

    private static AndroidSize ChooseSize(AndroidSize[] sizes, int width, int height)
    {
        if (sizes.Length == 0) return new AndroidSize(1920, 1080);
        var aspect = (double)width / height;
        // Smallest size covering the output, preferring the output's aspect ratio; otherwise the largest available.
        return sizes.Where(s => s.Width >= width && s.Height >= height)
                   .OrderBy(s => Math.Abs((double)s.Width / s.Height - aspect) > 0.01 ? 1 : 0).ThenBy(s => (long)s.Width * s.Height).FirstOrDefault()
               ?? sizes.OrderByDescending(s => (long)s.Width * s.Height).First();
    }

    private static (AndroidSize Size, AndroidRange Range)? HighSpeedChoice(StreamConfigurationMap map, int width, int height, double fps)
    {
        try
        {
            return (map.GetHighSpeedVideoSizes() ?? [])
                .SelectMany(size => (map.GetHighSpeedVideoFpsRangesFor(size) ?? []).Select(range => (Size: size, Range: range)))
                .Where(c => Upper(c.Range) >= Math.Ceiling(fps) - 0.01)
                .OrderBy(c => Math.Abs((long)c.Size.Width * c.Size.Height - (long)width * height))
                .ThenBy(c => Lower(c.Range) == Upper(c.Range) ? 0 : 1).ThenBy(c => Upper(c.Range))
                .Select(c => ((AndroidSize, AndroidRange)?)(c.Size, c.Range)).FirstOrDefault();
        }
        catch (Java.Lang.IllegalArgumentException) { return null; }
    }

    private void Opened(CameraDevice camera)
    {
        if (disposed) { camera.Close(); return; }
        device = camera;
        Status = "Starting camera…";
        try
        {
            var callback = new SessionCallback(this);
            IList<global::Android.Views.Surface> surfaces = [Texture.Surface];
#pragma warning disable CA1422 // Handler-based session creation remains supported and keeps callbacks on our thread.
            if (HighSpeed) camera.CreateConstrainedHighSpeedCaptureSession(surfaces, callback, handler);
            else camera.CreateCaptureSession(surfaces, callback, handler);
#pragma warning restore CA1422
        }
        catch (Exception ex) { Status = "Camera could not start: " + ex.Message; }
    }

    private void Configured(CameraCaptureSession configured)
    {
        if (disposed || device is null) { configured.Close(); return; }
        session = configured;
        try
        {
            var request = device.CreateCaptureRequest(CameraTemplate.Record);
            request.AddTarget(Texture.Surface);
            if (FpsRange is not null) request.Set(CaptureRequest.ControlAeTargetFpsRange!, FpsRange);
            if (!HighSpeed) request.Set(CaptureRequest.ControlAfMode!, Java.Lang.Integer.ValueOf((int)ControlAFMode.ContinuousVideo));
            if (HighSpeed && configured is CameraConstrainedHighSpeedCaptureSession highSpeed)
                highSpeed.SetRepeatingBurst(highSpeed.CreateHighSpeedRequestList(request.Build()), null, handler);
            else configured.SetRepeatingRequest(request.Build(), null, handler);
            Status = null;
        }
        catch (Exception ex) { Status = "Camera could not start: " + ex.Message; }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { session?.Close(); } catch { }
        try { device?.Close(); } catch { }
        session = null; device = null;
        Texture.Dispose();
    }

    public string Describe() => $"{Texture.Width}×{Texture.Height}" + (FpsRange is null ? "" : $" @ {Lower(FpsRange)}–{Upper(FpsRange)} fps") + (HighSpeed ? " (high speed)" : "")
        + (FpsRange is not null && Upper(FpsRange) < fps - 0.5 ? $" · this camera tops out below the requested {fps:0.##} fps" : "");

    private sealed class DeviceCallback(CameraSource owner) : CameraDevice.StateCallback
    {
        public override void OnOpened(CameraDevice camera) => owner.Opened(camera);
        public override void OnDisconnected(CameraDevice camera)
        {
            owner.Status = "Camera was taken by another app";
            try { camera.Close(); } catch { }
        }
        public override void OnError(CameraDevice camera, CameraError error)
        {
            owner.Status = error switch
            {
                CameraError.CameraInUse => "Camera is in use by another app",
                CameraError.MaxCamerasInUse => "This phone cannot run this camera at the same time as the other active camera",
                CameraError.CameraDisabled => "Camera is disabled by policy",
                _ => $"Camera error ({error})"
            };
            try { camera.Close(); } catch { }
        }
    }

    private sealed class SessionCallback(CameraSource owner) : CameraCaptureSession.StateCallback
    {
        public override void OnConfigured(CameraCaptureSession session) => owner.Configured(session);
        public override void OnConfigureFailed(CameraCaptureSession session)
        {
            owner.Status = "This camera configuration is not supported with the other active cameras";
            try { session.Close(); } catch { }
        }
    }
}
