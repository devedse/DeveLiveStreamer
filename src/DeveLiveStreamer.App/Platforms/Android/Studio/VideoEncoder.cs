using System.Diagnostics;
using System.Runtime.InteropServices;
using Android.Media;
using Android.OS;
using DeveLiveStreamer.Core;
using DeveLiveStreamer.Streaming.Media;
using AndroidSurface = Android.Views.Surface;

namespace DeveLiveStreamer.App.Platforms.Android.Studio;

/// <summary>
/// Hardware H.264/HEVC encoder fed by the compositor through an input surface. Requested settings are applied as far
/// as the device's encoder accepts them; anything it rejects is reported rather than silently lowered.
/// </summary>
internal sealed class VideoEncoder : IDisposable
{
    private readonly MediaCodec codec;
    private readonly Thread drain;
    private readonly long originUs;
    private volatile bool running = true;
    private bool started;
    private long framesThisSecond, secondStartUs;
    private long? clockOffsetUs;

    /// <param name="essentialOnly">
    /// Configure only size, bitrate, frame rate and keyframe interval. Some encoders accept tuning keys such as
    /// low-latency mode or real-time priority and then never produce output; this is the fallback for that case.
    /// </param>
    public VideoEncoder(OutputSettings settings, long originNs, List<string> notes, bool essentialOnly = false)
    {
        EssentialOnly = essentialOnly;
        originUs = originNs / 1000;
        Codec = settings.VideoCodec.StartsWith("H.265", StringComparison.Ordinal) ? VideoCodec.H265 : VideoCodec.H264;
        var mime = Codec == VideoCodec.H264 ? MediaFormat.MimetypeVideoAvc : MediaFormat.MimetypeVideoHevc;
        var fps = settings.Fps;
        var candidates = new MediaCodecList(MediaCodecListKind.RegularCodecs).GetCodecInfos()!
            .Where(c => c.IsEncoder && c.GetSupportedTypes()!.Contains(mime, StringComparer.OrdinalIgnoreCase))
            .OrderByDescending(c => c.IsHardwareAccelerated).ToList();
        if (candidates.Count == 0) throw new InvalidOperationException($"This phone has no {settings.VideoCodec} encoder.");
        var chosen = candidates.FirstOrDefault(c => Supports(c, mime, settings.OutputWidth, settings.OutputHeight, fps));
        if (chosen is null)
        {
            chosen = candidates[0];
            notes.Add($"{chosen.Name} does not advertise {settings.OutputWidth}×{settings.OutputHeight} at {fps:0.##} fps; trying it anyway.");
        }
        CodecName = chosen.Name!;
        var capabilities = chosen.GetCapabilitiesForType(mime)!;
        var encoderCapabilities = capabilities.EncoderCapabilities!;

        var optional = new List<(string Key, Action<MediaFormat> Apply, string Description)>();
        var rateMode = settings.RateControl == "CBR" ? BitrateMode.Cbr : BitrateMode.Vbr;
        if (encoderCapabilities.IsBitrateModeSupported(rateMode))
            optional.Add((MediaFormat.KeyBitrateMode, f => f.SetInteger(MediaFormat.KeyBitrateMode, (int)rateMode), settings.RateControl));
        else notes.Add($"{CodecName} does not support {settings.RateControl}; using the encoder's default rate control.");
        if (Codec == VideoCodec.H264 && ProfileFor(settings.Profile) is { } profile)
        {
            var level = capabilities.ProfileLevels?.Where(p => (int)p.Profile == profile).Select(p => (int)p.Level).DefaultIfEmpty(0).Max() ?? 0;
            if (level > 0) optional.Add((MediaFormat.KeyProfile, f => { f.SetInteger(MediaFormat.KeyProfile, profile); f.SetInteger(MediaFormat.KeyLevel, level); }, settings.Profile + " profile"));
            else notes.Add($"{CodecName} does not list the {settings.Profile} profile; using its default.");
        }
        if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
            optional.Add(("max-bframes", f => f.SetInteger("max-bframes", settings.BFrames), $"{settings.BFrames} B-frames"));
        optional.Add((MediaFormat.KeyOperatingRate, f => f.SetFloat(MediaFormat.KeyOperatingRate, (float)fps), "operating rate"));
        optional.Add(("latency", f => f.SetInteger("latency", 1), "low-latency output"));
        optional.Add((MediaFormat.KeyPriority, f => f.SetInteger(MediaFormat.KeyPriority, 0), "real-time priority"));
        if (essentialOnly) optional.Clear();

        codec = MediaCodec.CreateByCodecName(CodecName);
        // Try every optional key, then drop them from the least to the most important until the encoder accepts the format.
        Exception? last = null;
        for (var kept = optional.Count; kept >= 0; kept--)
        {
            var format = MediaFormat.CreateVideoFormat(mime, settings.OutputWidth, settings.OutputHeight);
            format.SetInteger(MediaFormat.KeyColorFormat, (int)MediaCodecCapabilities.Formatsurface);
            format.SetInteger(MediaFormat.KeyBitRate, checked(settings.VideoBitrateKbps * 1000));
            format.SetFloat(MediaFormat.KeyFrameRate, (float)fps);
            format.SetFloat(MediaFormat.KeyIFrameInterval, (float)settings.KeyframeSeconds);
            foreach (var option in optional.Take(kept)) option.Apply(format);
            try
            {
                codec.Configure(format, null, null, MediaCodecConfigFlags.Encode);
                foreach (var dropped in optional.Skip(kept)) notes.Add($"{CodecName} rejected {dropped.Description}; continuing without it.");
                last = null;
                break;
            }
            catch (Exception ex) when (ex is Java.Lang.Exception or MediaCodec.CodecException) { last = ex; codec.Reset(); }
        }
        if (last is not null)
        {
            codec.Release();
            throw new InvalidOperationException($"{CodecName} cannot encode {settings.OutputWidth}×{settings.OutputHeight} at {fps:0.##} fps " +
                $"and {settings.VideoBitrateKbps} kbps: {last.Message}");
        }
        InputSurface = codec.CreateInputSurface();
        codec.Start();
        drain = new Thread(Drain) { IsBackground = true, Name = "VideoEncoder", Priority = System.Threading.ThreadPriority.AboveNormal };
    }

    /// <summary>Begin consuming output after the publisher has subscribed to both format and frame events.</summary>
    public void Start()
    {
        if (started) throw new InvalidOperationException("The video encoder is already running.");
        started = true;
        drain.Start();
    }

    public bool EssentialOnly { get; }
    public void ReportStall(string message) => Failure ??= message;
    public VideoCodec Codec { get; }
    public string CodecName { get; }
    public AndroidSurface InputSurface { get; }
    public double OutputFps { get; private set; }
    public event Action<VideoFormat>? FormatReady;
    public event Action<VideoFrame>? FrameReady;
    public string? Failure { get; private set; }
    /// <summary>Frames the encoder returned; for diagnostics.</summary>
    public long FramesEncoded;
    public bool HasFormat { get; private set; }

    private static bool Supports(MediaCodecInfo codec, string mime, int width, int height, double fps)
    {
        try { return codec.GetCapabilitiesForType(mime)?.VideoCapabilities?.AreSizeAndRateSupported(width, height, fps) == true; }
        catch (Java.Lang.Exception) { return false; }
    }

    private static int? ProfileFor(string name) => name switch
    {
        "Baseline" => (int)MediaCodecProfileType.Avcprofilebaseline,
        "Main" => (int)MediaCodecProfileType.Avcprofilemain,
        "High" => (int)MediaCodecProfileType.Avcprofilehigh,
        _ => null
    };

    public void RequestKeyFrame()
    {
        try { using var parameters = new Bundle(); parameters.PutInt(MediaCodec.ParameterKeyRequestSyncFrame, 0); codec.SetParameters(parameters); }
        catch (Java.Lang.IllegalStateException) { }
    }

    private void Drain()
    {
        var info = new MediaCodec.BufferInfo();
        try
        {
            while (running)
            {
                var index = codec.DequeueOutputBuffer(info, 20_000);
                if (index == (int)MediaCodecInfoState.OutputFormatChanged)
                {
                    using var output = codec.OutputFormat;
                    var sets = new List<byte[]>();
                    foreach (var key in new[] { "csd-0", "csd-1", "csd-2" })
                        if (output.ContainsKey(key) && output.GetByteBuffer(key) is { } buffer)
                        {
                            using var _ = buffer;
                            var bytes = new byte[buffer.Remaining()];
                            buffer.Get(bytes);
                            sets.AddRange(NalUnits.ParseAnnexB(bytes));
                        }
                    global::Android.Util.Log.Info("DeveLiveStreamer", $"{CodecName} output format {output}");
                    if (sets.Count > 0) { HasFormat = true; FormatReady?.Invoke(VideoFormat.FromParameterSets(Codec, sets, 0)); }
                    continue;
                }
                if (index < 0) continue;
                var data = new byte[info.Size];
                if (info.Size > 0) { using var buffer = codec.GetOutputBuffer(index)!; Marshal.Copy(buffer.GetDirectBufferAddress() + info.Offset, data, 0, info.Size); }
                codec.ReleaseOutputBuffer(index, false);
                if ((info.Flags & MediaCodecBufferFlags.EndOfStream) != 0) break;
                if ((info.Flags & MediaCodecBufferFlags.CodecConfig) != 0)
                {
                    HasFormat = true;
                    FormatReady?.Invoke(VideoFormat.FromParameterSets(Codec, NalUnits.ParseAnnexB(data), 0));
                    continue;
                }
                if (data.Length == 0) continue;
                if (Interlocked.Increment(ref FramesEncoded) <= 3)
                    global::Android.Util.Log.Info("DeveLiveStreamer", $"{CodecName} frame {FramesEncoded}: {data.Length} bytes, pts {info.PresentationTimeUs} us, flags {info.Flags}");
                var pts = info.PresentationTimeUs - originUs;
                // Timestamps normally come back exactly as the compositor stamped them (our clock). If an encoder
                // returns them on another clock, rebase once so frames are not silently discarded.
                var elapsedUs = Stopwatch.GetTimestamp() * (1_000_000 / (double)Stopwatch.Frequency) - originUs;
                if (clockOffsetUs is null)
                {
                    clockOffsetUs = pts < 0 || pts > elapsedUs + 1_000_000 ? pts - (long)elapsedUs : 0;
                    if (clockOffsetUs != 0)
                        global::Android.Util.Log.Warn("DeveLiveStreamer", $"{CodecName} returned pts {info.PresentationTimeUs} us outside our clock; rebasing by {clockOffsetUs} us");
                }
                pts -= clockOffsetUs ?? 0;
                if (pts < 0) continue;
                FrameReady?.Invoke(new VideoFrame(data, pts, pts, (info.Flags & MediaCodecBufferFlags.KeyFrame) != 0));
                framesThisSecond++;
                if (info.PresentationTimeUs - secondStartUs >= 1_000_000)
                {
                    OutputFps = secondStartUs == 0 ? 0 : framesThisSecond * 1e6 / (info.PresentationTimeUs - secondStartUs);
                    framesThisSecond = 0; secondStartUs = info.PresentationTimeUs;
                }
            }
        }
        catch (Exception ex) when (running) { Failure = Studio.Describe("Video encoder stopped", ex); }
        catch { }
    }

    public void Dispose()
    {
        running = false;
        try { codec.SignalEndOfInputStream(); } catch { }
        if (started) drain.Join(1000);
        try { codec.Stop(); } catch { }
        try { codec.Release(); } catch { }
        InputSurface.Release();
    }
}
