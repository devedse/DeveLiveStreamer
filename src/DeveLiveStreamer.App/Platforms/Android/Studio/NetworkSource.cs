using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Android.Media;
using Android.OS;
using DeveLiveStreamer.Streaming.Audio;
using DeveLiveStreamer.Streaming.Ingest;
using DeveLiveStreamer.Streaming.Media;
using StreamAudioFormat = DeveLiveStreamer.Streaming.Media.AudioFormat;

namespace DeveLiveStreamer.App.Platforms.Android.Studio;

/// <summary>
/// An incoming RTMP/SRT stream decoded with MediaCodec: video into an <see cref="ExternalTexture"/>, audio into the
/// session's mixer. Frames are presented on their own timestamps with a small jitter buffer.
/// </summary>
internal sealed class NetworkSource : IDisposable
{
    private const long JitterBufferUs = 150_000;
    private readonly IncomingStream incoming;
    private readonly Func<AudioMixer?> mixer;
    private readonly BlockingCollection<VideoFrame> videoFrames = new(new ConcurrentQueue<VideoFrame>(), 90);
    private readonly BlockingCollection<AudioFrame> audioFrames = new(new ConcurrentQueue<AudioFrame>(), 200);
    private readonly CancellationTokenSource stopping = new();
    private readonly Thread videoThread, audioThread;
    private VideoFormat? pendingVideo;
    private StreamAudioFormat? pendingAudio;
    private AudioMixer? attachedMixer;
    private AudioMixerInput? mixerInput;
    private volatile bool audioEnabled;

    public NetworkSource(string url, bool audioEnabled, Func<AudioMixer?> mixer, Handler handler)
    {
        Url = url;
        this.mixer = mixer;
        this.audioEnabled = audioEnabled;
        Texture = new ExternalTexture(1920, 1080, handler);
        incoming = IncomingStream.Create(url);
        incoming.VideoFormatChanged += f => Volatile.Write(ref pendingVideo, f);
        incoming.AudioFormatChanged += f => Volatile.Write(ref pendingAudio, f);
        incoming.VideoFrameReceived += f => { if (!videoFrames.TryAdd(f)) DropOldest(videoFrames, f); };
        incoming.AudioFrameReceived += f => { if (this.audioEnabled && !audioFrames.TryAdd(f)) DropOldest(audioFrames, f); };
        videoThread = new Thread(VideoLoop) { IsBackground = true, Name = "NetworkVideoDecoder" };
        audioThread = new Thread(AudioLoop) { IsBackground = true, Name = "NetworkAudioDecoder" };
        videoThread.Start(); audioThread.Start();
        incoming.Start();
    }

    public string Url { get; }
    public ExternalTexture Texture { get; }
    public double Aspect { get; private set; } = 16 / 9d;
    public volatile string? DecoderError;
    public bool AudioEnabled { get => audioEnabled; set => audioEnabled = value; }
    public string? Status => DecoderError ?? (incoming.State == IncomingState.Receiving && Texture.HasFrame ? null : incoming.StatusText);
    public string Description => incoming.StatusText;

    private static void DropOldest<T>(BlockingCollection<T> queue, T item)
    {
        queue.TryTake(out _);
        queue.TryAdd(item);
    }

    private void VideoLoop()
    {
        MediaCodec? decoder = null;
        VideoFormat? configured = null;
        var info = new MediaCodec.BufferInfo();
        long anchorUs = 0, anchorTicks = 0;
        var anchored = false;
        try
        {
            while (!stopping.IsCancellationRequested)
            {
                if (!videoFrames.TryTake(out var frame, 20, stopping.Token)) { Drain(); continue; }
                var format = Volatile.Read(ref pendingVideo);
                if (format is null) continue;
                if (configured is null || !configured.SameParameterSets(format))
                {
                    if (!frame.IsKeyFrame) continue;
                    Release(ref decoder);
                    decoder = Configure(format);
                    configured = format;
                    anchored = false;
                    if (decoder is null) continue;
                }
                if (decoder is null) continue;
                var index = decoder.DequeueInputBuffer(20_000);
                if (index >= 0)
                {
                    using var buffer = decoder.GetInputBuffer(index)!;
                    var length = Math.Min(frame.Data.Length, buffer.Capacity());
                    Marshal.Copy(frame.Data, 0, buffer.GetDirectBufferAddress(), length);
                    decoder.QueueInputBuffer(index, 0, length, frame.PtsUs, frame.IsKeyFrame ? MediaCodecBufferFlags.KeyFrame : MediaCodecBufferFlags.None);
                }
                Drain();
            }
        }
        catch (System.OperationCanceledException) { }
        catch (Exception ex) { DecoderError = "Video decoding failed: " + ex.Message; }
        finally { Release(ref decoder); }

        void Drain()
        {
            if (decoder is null) return;
            while (true)
            {
                var index = decoder.DequeueOutputBuffer(info, 0);
                if (index == (int)MediaCodecInfoState.OutputFormatChanged)
                {
                    using var output = decoder.OutputFormat;
                    var width = output.GetInteger(MediaFormat.KeyWidth);
                    var height = output.GetInteger(MediaFormat.KeyHeight);
                    if (output.ContainsKey("crop-right"))
                    {
                        width = output.GetInteger("crop-right") - output.GetInteger("crop-left") + 1;
                        height = output.GetInteger("crop-bottom") - output.GetInteger("crop-top") + 1;
                    }
                    if (width > 0 && height > 0) Aspect = (double)width / height;
                    continue;
                }
                if (index < 0) return;
                // Present on the stream's own clock behind a short jitter buffer; re-anchor after stalls or jumps.
                var now = Stopwatch.GetTimestamp();
                if (!anchored) { anchorUs = info.PresentationTimeUs; anchorTicks = now; anchored = true; }
                var dueUs = info.PresentationTimeUs - anchorUs + JitterBufferUs;
                var elapsedUs = (long)Stopwatch.GetElapsedTime(anchorTicks, now).TotalMicroseconds;
                if (dueUs - elapsedUs > 2_000_000 || elapsedUs - dueUs > 1_000_000) { anchorUs = info.PresentationTimeUs; anchorTicks = now; dueUs = JitterBufferUs; elapsedUs = 0; }
                if (dueUs > elapsedUs) Thread.Sleep(TimeSpan.FromMicroseconds(Math.Min(dueUs - elapsedUs, 100_000)));
                decoder.ReleaseOutputBuffer(index, true);
            }
        }
    }

    private MediaCodec? Configure(VideoFormat format)
    {
        var mime = format.Codec == VideoCodec.H264 ? MediaFormat.MimetypeVideoAvc : MediaFormat.MimetypeVideoHevc;
        var mediaFormat = MediaFormat.CreateVideoFormat(mime, format.Width, format.Height);
        if (format.Codec == VideoCodec.H264)
        {
            mediaFormat.SetByteBuffer("csd-0", Java.Nio.ByteBuffer.Wrap(NalUnits.ToAnnexB(format.ParameterSets.Where(n => NalUnits.Type(format.Codec, n[0]) == 7))));
            mediaFormat.SetByteBuffer("csd-1", Java.Nio.ByteBuffer.Wrap(NalUnits.ToAnnexB(format.ParameterSets.Where(n => NalUnits.Type(format.Codec, n[0]) == 8))));
        }
        else mediaFormat.SetByteBuffer("csd-0", Java.Nio.ByteBuffer.Wrap(NalUnits.ToAnnexB(format.ParameterSets)));
        mediaFormat.SetInteger(MediaFormat.KeyPriority, 0);
        try
        {
            var decoder = MediaCodec.CreateDecoderByType(mime);
            decoder.Configure(mediaFormat, Texture.Surface, null, MediaCodecConfigFlags.None);
            decoder.Start();
            Aspect = (double)format.Width / format.Height;
            DecoderError = null;
            return decoder;
        }
        catch (Exception ex)
        {
            DecoderError = $"This phone cannot decode the incoming {(format.Codec == VideoCodec.H264 ? "H.264" : "HEVC")} {format.Width}×{format.Height} stream: {ex.Message}";
            return null;
        }
    }

    private void AudioLoop()
    {
        MediaCodec? decoder = null;
        StreamAudioFormat? configured = null;
        var info = new MediaCodec.BufferInfo();
        int rate = 48000, channels = 2;
        short[] pcm = [];
        try
        {
            while (!stopping.IsCancellationRequested)
            {
                if (!audioFrames.TryTake(out var frame, 20, stopping.Token)) { Drain(); continue; }
                var format = Volatile.Read(ref pendingAudio);
                if (format is null) continue;
                if (configured is null || !configured.AudioSpecificConfig.AsSpan().SequenceEqual(format.AudioSpecificConfig))
                {
                    Release(ref decoder);
                    var mediaFormat = MediaFormat.CreateAudioFormat(MediaFormat.MimetypeAudioAac, format.SampleRate, format.Channels);
                    mediaFormat.SetByteBuffer("csd-0", Java.Nio.ByteBuffer.Wrap(format.AudioSpecificConfig));
                    decoder = MediaCodec.CreateDecoderByType(MediaFormat.MimetypeAudioAac);
                    decoder.Configure(mediaFormat, null, null, MediaCodecConfigFlags.None);
                    decoder.Start();
                    configured = format; rate = format.SampleRate; channels = format.Channels;
                }
                var index = decoder!.DequeueInputBuffer(20_000);
                if (index >= 0)
                {
                    using var buffer = decoder.GetInputBuffer(index)!;
                    Marshal.Copy(frame.Data, 0, buffer.GetDirectBufferAddress(), Math.Min(frame.Data.Length, buffer.Capacity()));
                    decoder.QueueInputBuffer(index, 0, Math.Min(frame.Data.Length, buffer.Capacity()), frame.PtsUs, MediaCodecBufferFlags.None);
                }
                Drain();
            }
        }
        catch (System.OperationCanceledException) { }
        catch (Exception ex) { DecoderError ??= "Audio decoding failed: " + ex.Message; }
        finally { Release(ref decoder); DetachMixer(); }

        void Drain()
        {
            if (decoder is null) return;
            while (true)
            {
                var index = decoder.DequeueOutputBuffer(info, 0);
                if (index == (int)MediaCodecInfoState.OutputFormatChanged)
                {
                    using var output = decoder.OutputFormat;
                    rate = output.GetInteger(MediaFormat.KeySampleRate);
                    channels = output.GetInteger(MediaFormat.KeyChannelCount);
                    continue;
                }
                if (index < 0) return;
                var samples = info.Size / 2;
                if (pcm.Length < samples) pcm = new short[samples];
                using (var buffer = decoder.GetOutputBuffer(index)!) Marshal.Copy(buffer.GetDirectBufferAddress() + info.Offset, pcm, 0, samples);
                decoder.ReleaseOutputBuffer(index, false);
                if (audioEnabled && MixerInput() is { } input) input.Write(pcm.AsSpan(0, samples), rate, channels);
            }
        }
    }

    private AudioMixerInput? MixerInput()
    {
        var current = mixer();
        if (current != attachedMixer)
        {
            if (mixerInput is not null) attachedMixer?.RemoveInput(mixerInput);
            attachedMixer = current;
            mixerInput = current?.AddInput("network");
        }
        return mixerInput;
    }

    private void DetachMixer()
    {
        if (mixerInput is not null) attachedMixer?.RemoveInput(mixerInput);
        mixerInput = null; attachedMixer = null;
    }

    private static void Release(ref MediaCodec? codec)
    {
        if (codec is null) return;
        try { codec.Stop(); } catch { }
        try { codec.Release(); } catch { }
        codec = null;
    }

    /// <summary>
    /// Signals the network and decoder threads to stop without waiting for them, so the compositor never stalls;
    /// the texture is disposed separately on the compositor thread.
    /// </summary>
    public void Dispose()
    {
        stopping.Cancel();
        _ = incoming.DisposeAsync().AsTask();
    }
}
