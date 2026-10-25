using System.Net.NetworkInformation;
using System.Net.Sockets;
using Android.Content;
using Android.Content.PM;
using Android.Hardware.Display;
using Android.Media.Projection;
using Android.OS;
using Android.Util;
using DeveLiveStreamer.App.Studio;
using DeveLiveStreamer.Core;
using DeveLiveStreamer.Streaming;
using DeveLiveStreamer.Streaming.Publishing;
using AndroidSurface = Android.Views.Surface;

namespace DeveLiveStreamer.App.Platforms.Android.Studio;

/// <summary>Android implementation of the studio: GPU compositor, capture sources, hardware encoders and SRT/RTMP output.</summary>
internal sealed class AndroidStudioEngine : IStudioEngine
{
    private static AndroidStudioEngine? instance;
    private readonly Context context = global::Android.App.Application.Context;
    private readonly Handler callbacks;
    private readonly Compositor compositor;
    private readonly SemaphoreSlim transitions = new(1, 1);
    private readonly RotationListener rotation;
    private LiveSession? session;
    private StudioStatus status = StudioStatus.Offline;
    private Timer? statistics;

    private AndroidStudioEngine()
    {
        var thread = new HandlerThread("StudioCallbacks");
        thread.Start();
        callbacks = new Handler(thread.Looper!);
        compositor = new Compositor(context, callbacks) { CameraPermitted = CameraPermitted };
        compositor.SourcesChanged += () => MainThread.BeginInvokeOnMainThread(() => SourcesChanged?.Invoke(this, EventArgs.Empty));
        rotation = new RotationListener(this);
        var displays = (DisplayManager)context.GetSystemService(Context.DisplayService)!;
        displays.RegisterDisplayListener(rotation, callbacks);
        rotation.OnDisplayChanged(0);
    }

    public static AndroidStudioEngine Instance => instance ??= new AndroidStudioEngine();

    public bool IsSupported => true;
    public StudioStatus Status => status;
    public event EventHandler? StatusChanged;
    public event EventHandler? SourcesChanged;
    public bool CameraPermitted => context.CheckSelfPermission(global::Android.Manifest.Permission.Camera) == Permission.Granted;

    public void UpdateScene(ProjectDocument project)
    {
        compositor.UpdateScene(RenderScene.From(project));
        // While live the session's output configuration stays fixed; the next stream picks up changes.
        if (session is null) compositor.SetOutput(project.Output.OutputWidth, project.Output.OutputHeight, project.Output.Fps);
    }

    public void AttachPreview(AndroidSurface surface, int width, int height) => compositor.AttachPreview(surface, width, height);
    public void ResizePreview(int width, int height) => compositor.ResizePreview(width, height);
    public void DetachPreview() => compositor.DetachPreview();
    public string? SourceStatus(string sourceId) => compositor.SourceStatus(sourceId);

    public async Task<bool> RequestCameraAsync()
    {
        var granted = await MainThread.InvokeOnMainThreadAsync(() => Permissions.RequestAsync<Permissions.Camera>()) == PermissionStatus.Granted;
        compositor.CameraPermitted = granted;
        return granted;
    }

    public IReadOnlyList<string> LocalAddresses()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => a.Address.ToString()).Distinct().ToList();
        }
        catch (NetworkInformationException) { return []; }
    }

    public async Task StartAsync(ProjectDocument project)
    {
        await transitions.WaitAsync();
        try
        {
            if (session is not null) return;
            var settings = ProjectDocument.Parse(project.Serialize()).Output;
            settings.Validate();
            var endpoint = StreamEndpoint.Parse(settings.ServerUrl, settings.StreamKey);
            if (compositor.FatalError is { } fatal) throw new InvalidOperationException(fatal);
            var notes = new List<string>();
            SetStatus(new StudioStatus(LiveState.Starting, "Preparing your stream…", TimeSpan.Zero, 0, 0, 0, 0, null, Describe(endpoint), []));

            var usesCamera = project.Sources.Any(s => s.Kind == SourceKind.Camera);
            var usesScreen = project.Sources.Any(s => s.Kind == SourceKind.Screen);
            if (usesCamera && !CameraPermitted && !await RequestCameraAsync()) notes.Add("Camera access was not allowed; camera sources stay empty.");
            if (settings.MicrophoneEnabled || settings.DeviceAudioEnabled)
            {
                var microphone = await MainThread.InvokeOnMainThreadAsync(() => Permissions.RequestAsync<Permissions.Microphone>());
                if (microphone != PermissionStatus.Granted)
                {
                    notes.Add("Microphone access was not allowed; streaming without microphone or device audio.");
                    settings.MicrophoneEnabled = settings.DeviceAudioEnabled = false;
                }
            }
            if (OperatingSystem.IsAndroidVersionAtLeast(33))
                await MainThread.InvokeOnMainThreadAsync(() => Permissions.RequestAsync<Permissions.PostNotifications>());
            Intent? consent = null;
            if (usesScreen || settings.DeviceAudioEnabled)
            {
                consent = await ScreenCaptureConsent.RequestAsync(context);
                if (consent is null) notes.Add("Screen capture was declined; the device screen source and device audio stay off.");
            }

            var types = consent is not null ? ForegroundService.TypeMediaProjection : (ForegroundService)0;
            // Camera and microphone service types exist from Android 11; Android 10 only needs the projection type.
            if (OperatingSystem.IsAndroidVersionAtLeast(30))
            {
                if (usesCamera && CameraPermitted) types |= ForegroundService.TypeCamera;
                if (settings.MicrophoneEnabled || settings.DeviceAudioEnabled) types |= ForegroundService.TypeMicrophone;
            }
            if (types == 0) types = ForegroundService.TypeMediaPlayback;
            await StreamingService.StartAsync(context, types);

            try
            {
                MediaProjection? projection = null;
                if (consent is not null)
                    projection = ((MediaProjectionManager)context.GetSystemService(Context.MediaProjectionService)!).GetMediaProjection((int)global::Android.App.Result.Ok, consent);
                session = await Task.Run(() => LiveSession.Start(this, settings, endpoint, projection, notes));
            }
            catch
            {
                StreamingService.Stop(context);
                throw;
            }
            KeepScreenOn(true);
            statistics = new Timer(_ => { Watchdog(); Refresh(); }, null, 0, 1000);
        }
        catch (Exception ex)
        {
            SetStatus(StudioStatus.Offline with { Message = ex.Message });
            throw;
        }
        finally { transitions.Release(); }
    }

    public async Task StopAsync()
    {
        await transitions.WaitAsync();
        try
        {
            if (session is not { } current) return;
            SetStatus(status with { State = LiveState.Stopping, Message = "Ending stream…" });
            statistics?.Dispose(); statistics = null;
            session = null;
            await current.StopAsync();
            StreamingService.Stop(context);
            KeepScreenOn(false);
            SetStatus(StudioStatus.Offline with { Message = $"Stream ended after {Format(current.Publisher.Statistics.LiveDuration)}." });
        }
        finally { transitions.Release(); }
    }

    /// <summary>Restarts a video encoder that accepts frames but produces nothing (about two seconds' worth).</summary>
    private void Watchdog()
    {
        if (!transitions.Wait(0)) return;
        try
        {
            if (session is not { } current || current.Video.HasFormat || current.Video.Failure is not null) return;
            var submitted = Interlocked.Read(ref compositor.FramesToEncoder);
            if (submitted < Math.Max(30, current.Settings.Fps * 2)) return;
            try
            {
                if (!current.RestartVideoWithEssentialSettings())
                    current.Video.ReportStall($"{current.Video.CodecName} produced no video from {submitted} frames, even with only essential settings.");
            }
            catch (Exception ex) { current.Video.ReportStall(Studio.Describe("The video encoder could not be restarted", ex)); }
        }
        finally { transitions.Release(); }
    }

    private void Refresh()
    {
        if (session is not { } current) return;
        if (current.Audio.Failure is { } audioFailure && !current.AudioAbandoned && current.Publisher.AbandonAudio())
        {
            current.AudioAbandoned = true;
            current.Notes.Add($"{audioFailure}. Streaming video without audio; end and restart the stream to try audio again.");
        }
        // A stalled audio encoder must not hold the stream back: once video is ready, wait a few seconds for audio at most.
        if (current.Video.HasFormat && !current.Audio.HasFormat && !current.AudioAbandoned)
        {
            current.VideoReadyAt ??= DateTime.UtcNow;
            if (DateTime.UtcNow - current.VideoReadyAt > TimeSpan.FromSeconds(5) && current.Publisher.AbandonAudio())
            {
                current.AudioAbandoned = true;
                current.Notes.Add($"The AAC encoder produced no audio ({Interlocked.Read(ref current.Audio.BlocksQueued)} blocks queued). " +
                    "Streaming video without audio; end and restart the stream to try audio again.");
            }
        }
        var stats = current.Publisher.Statistics;
        var failure = current.Video.Failure ?? (current.AudioAbandoned ? null : current.Audio.Failure) ?? compositor.FatalError;
        var state = stats.State switch
        {
            PublisherState.Live => LiveState.Live,
            PublisherState.Reconnecting => LiveState.Reconnecting,
            _ => LiveState.Connecting
        };
        var message = failure ?? stats.State switch
        {
            PublisherState.Live => "",
            PublisherState.Reconnecting => "Reconnecting" + (stats.LastError is null ? "…" : $": {stats.LastError}"),
            PublisherState.Idle => "Starting encoders…",
            _ => $"Connecting to {current.Destination}…"
        };
        var warnings = current.Notes.ToList();
        if (compositor.CameraSummary is { Length: > 0 } cameras) warnings.Add("Capture: " + cameras);
        if (stats.State != PublisherState.Live)
            warnings.Add($"Pipeline: {Interlocked.Read(ref compositor.FramesToEncoder)} frames to the video encoder, {Interlocked.Read(ref current.Video.FramesEncoded)} encoded" +
                $"{(current.Video.HasFormat ? "" : " (no format yet)")}; {Interlocked.Read(ref current.Audio.BlocksQueued)} audio blocks, " +
                $"{Interlocked.Read(ref current.Audio.FramesEncoded)} encoded{(current.Audio.HasFormat || current.AudioAbandoned ? "" : " (no format yet)")}.");
        if (stats.Srt is { } srt && srt.PacketsLost + srt.PacketsDropped > 0)
            warnings.Add($"SRT: {srt.PacketsLost} lost, {srt.PacketsRetransmitted} retransmitted, {srt.PacketsDropped} dropped packets; latency {srt.NegotiatedLatencyMs} ms.");
        SetStatus(new StudioStatus(state, message, stats.LiveDuration, stats.BitrateKbps, compositor.RenderFps, current.Video.OutputFps,
            stats.DroppedFrames + Interlocked.Read(ref compositor.DroppedFrames), stats.Srt?.RttMs, current.Destination, warnings));
    }

    private void SetStatus(StudioStatus value)
    {
        status = value;
        MainThread.BeginInvokeOnMainThread(() => StatusChanged?.Invoke(this, EventArgs.Empty));
    }

    private static void KeepScreenOn(bool on) => MainThread.BeginInvokeOnMainThread(() =>
    {
        var window = Platform.CurrentActivity?.Window;
        if (on) window?.AddFlags(global::Android.Views.WindowManagerFlags.KeepScreenOn);
        else window?.ClearFlags(global::Android.Views.WindowManagerFlags.KeepScreenOn);
    });

    internal static string Describe(StreamEndpoint endpoint) => endpoint switch
    {
        SrtEndpoint srt => $"SRT {srt.Host}:{srt.Port}" + (srt.StreamId is { Length: > 0 } id ? $" · {id}" : "") + (srt.Passphrase is null ? "" : " · encrypted"),
        RtmpEndpoint rtmp => $"{(rtmp.Tls ? "RTMPS" : "RTMP")} {rtmp.Host}:{rtmp.Port}/{rtmp.App}",
        _ => endpoint.Host
    };

    private static string Format(TimeSpan duration) => duration.TotalHours >= 1 ? duration.ToString(@"h\:mm\:ss") : duration.ToString(@"m\:ss");

    private (int Width, int Height, int Dpi) ScreenSize()
    {
        var display = ((DisplayManager)context.GetSystemService(Context.DisplayService)!).GetDisplay(global::Android.Views.Display.DefaultDisplay)!;
        var metrics = new DisplayMetrics();
#pragma warning disable CA1422 // Real metrics of the default display are what MediaProjection mirrors.
        display.GetRealMetrics(metrics);
#pragma warning restore CA1422
        return (metrics.WidthPixels & ~1, metrics.HeightPixels & ~1, (int)metrics.DensityDpi);
    }

    private sealed class LiveSession
    {
        public required VideoEncoder Video { get; set; }
        public required OutputSettings Settings { get; init; }
        public required long OriginNs { get; init; }
        public required AudioPipeline Audio { get; init; }
        public required LivePublisher Publisher { get; init; }
        public required List<string> Notes { get; init; }
        public required string Destination { get; init; }
        public MediaProjection? Projection { get; init; }
        public required AndroidStudioEngine Engine { get; init; }
        public bool AudioAbandoned { get; set; }
        public DateTime? VideoReadyAt { get; set; }

        public static LiveSession Start(AndroidStudioEngine engine, OutputSettings settings, StreamEndpoint endpoint, MediaProjection? projection, List<string> notes)
        {
            var compositor = engine.compositor;
            var origin = System.Diagnostics.Stopwatch.GetTimestamp() * (1_000_000_000 / System.Diagnostics.Stopwatch.Frequency);
            VideoEncoder? video = null; AudioPipeline? audio = null;
            LivePublisher? publisher = null;
            try
            {
                video = new VideoEncoder(settings, origin, notes);
                if (projection is not null)
                {
                    var (width, height, dpi) = engine.ScreenSize();
                    compositor.StartScreenCapture(projection, width, height, dpi);
                }
                audio = new AudioPipeline(settings, origin, projection, notes);
                var mixer = audio.Mixer;
                compositor.Mixer = () => mixer;
                publisher = new LivePublisher(new PublishOptions(endpoint, true, true, settings.SrtLatencyMs, settings.VideoBitrateKbps, settings.AudioBitrateKbps));
                Connect(video, publisher, settings);
                audio.FormatReady += f => { Log.Info("DeveLiveStreamer", $"Audio format AAC {f.SampleRate} Hz x{f.Channels}"); publisher.SetAudioFormat(f); };
                audio.FrameReady += publisher.Push;
                publisher.StateChanged += (state, message) =>
                {
                    Log.Info("DeveLiveStreamer", $"Publisher {state}{(message is null ? "" : ": " + message)}");
                    engine.Refresh();
                };
                notes.Insert(0, $"Encoder: {video.CodecName} ({settings.VideoCodec}, {settings.OutputWidth}×{settings.OutputHeight} @ {settings.Fps:0.###} fps, {settings.VideoBitrateKbps} kbps)");
                var session = new LiveSession
                {
                    Video = video, Audio = audio, Publisher = publisher, Notes = notes, Destination = Describe(endpoint),
                    Projection = projection, Engine = engine, Settings = settings, OriginNs = origin
                };
                publisher.KeyFrameRequested += () => session.Video.RequestKeyFrame();
                // A codec may emit its format immediately. Subscribe every consumer before any worker or video input starts.
                publisher.Start();
                video.Start();
                audio.Start();
                compositor.StartEncoding(video.InputSurface, settings.OutputWidth, settings.OutputHeight, settings.Fps);
                return session;
            }
            catch
            {
                compositor.Mixer = () => null;
                try { compositor.StopEncoding(); } catch { }
                try { compositor.StopScreenCapture(); } catch { }
                audio?.Dispose(); video?.Dispose();
                publisher?.StopAsync().GetAwaiter().GetResult();
                projection?.Stop();
                throw;
            }
        }

        private static void Connect(VideoEncoder encoder, LivePublisher publisher, OutputSettings settings)
        {
            encoder.FormatReady += f =>
            {
                Log.Info("DeveLiveStreamer", $"Video format {f.Codec} {f.Width}x{f.Height} from {encoder.CodecName}");
                publisher.SetVideoFormat(f with { FrameRate = settings.Fps });
            };
            encoder.FrameReady += publisher.Push;
        }

        /// <summary>
        /// Rebuilds the video encoder with only the essential settings when the configured one takes frames but
        /// returns nothing. Returns false when there is nothing left to try.
        /// </summary>
        public bool RestartVideoWithEssentialSettings()
        {
            if (Video.EssentialOnly) return false;
            var compositor = Engine.compositor;
            var failed = Video;
            var submitted = Interlocked.Read(ref compositor.FramesToEncoder);
            compositor.StopEncoding();
            failed.Dispose();
            var video = new VideoEncoder(Settings, OriginNs, Notes, essentialOnly: true);
            Connect(video, Publisher, Settings);
            Video = video;
            video.Start();
            compositor.StartEncoding(video.InputSurface, Settings.OutputWidth, Settings.OutputHeight, Settings.Fps);
            Notes.Add($"{failed.CodecName} returned nothing for {submitted} frames with low-latency, priority, profile and rate-control tuning; " +
                "restarted it with only resolution, frame rate, bitrate and keyframe interval.");
            Log.Warn("DeveLiveStreamer", Notes[^1]);
            return true;
        }

        public async Task StopAsync()
        {
            await Publisher.StopAsync();
            var compositor = Engine.compositor;
            try { compositor.StopEncoding(); } catch { }
            try { compositor.StopScreenCapture(); } catch { }
            compositor.Mixer = () => null;
            Audio.Dispose();
            Video.Dispose();
            try { Projection?.Stop(); } catch { }
        }
    }

    private sealed class RotationListener(AndroidStudioEngine owner) : Java.Lang.Object, DisplayManager.IDisplayListener
    {
        public void OnDisplayAdded(int displayId) { }
        public void OnDisplayRemoved(int displayId) { }
        public void OnDisplayChanged(int displayId)
        {
            var display = ((DisplayManager)owner.context.GetSystemService(Context.DisplayService)!).GetDisplay(global::Android.Views.Display.DefaultDisplay);
            if (display is null) return;
            owner.compositor.DisplayRotation = (int)display.Rotation * 90;
            if (owner.session?.Projection is not null)
            {
                var (width, height, dpi) = owner.ScreenSize();
                owner.compositor.ResizeScreenCapture(width, height, dpi);
            }
        }
    }
}
