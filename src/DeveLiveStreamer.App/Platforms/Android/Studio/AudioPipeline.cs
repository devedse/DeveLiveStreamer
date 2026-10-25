using System.Diagnostics;
using System.Runtime.InteropServices;
using Android.Media;
using Android.Media.Projection;
using DeveLiveStreamer.Core;
using DeveLiveStreamer.Streaming.Audio;
using DeveLiveStreamer.Streaming.Media;
using AndroidAudioFormat = Android.Media.AudioFormat;
using StreamAudioFormat = DeveLiveStreamer.Streaming.Media.AudioFormat;

namespace DeveLiveStreamer.App.Platforms.Android.Studio;

/// <summary>
/// Microphone and device-audio capture mixed with network-source audio, encoded to AAC-LC.
/// </summary>
internal sealed class AudioPipeline : IDisposable
{
    private const int FramesPerBlock = Aac.SamplesPerFrame;
    private readonly int sampleRate, channels;
    private readonly long originNs;
    private readonly MediaFormat encoderFormat;
    private readonly object codecLock = new();
    private MediaCodec encoder;
    private int restarts;
    private readonly AudioRecord? microphone;
    private AudioRecord? device;
    private readonly AudioMixerInput? microphoneInput, deviceInput;
    private readonly Thread driver, drain;
    private readonly Thread? deviceThread, microphoneThread;
    private volatile bool running = true;
    private bool started;
    private volatile bool deviceCapturing = true;

    public AudioPipeline(OutputSettings settings, long originNs, MediaProjection? projection, List<string> notes)
    {
        sampleRate = settings.AudioSampleRate;
        channels = settings.Stereo ? 2 : 1;
        this.originNs = originNs;
        Mixer = new AudioMixer(sampleRate, channels);
        if (settings.MicrophoneEnabled)
        {
            microphone = CreateRecord(() => new AudioRecord.Builder().SetAudioSource(AudioSource.Mic)!, notes, "microphone");
            if (microphone is not null) microphoneInput = Mixer.AddInput("microphone", 200);
        }
        if (settings.DeviceAudioEnabled)
        {
            if (projection is null) notes.Add("Device audio needs screen-capture permission; streaming without it.");
            else
            {
                device = CreateRecord(() =>
                {
                    var capture = new AudioPlaybackCaptureConfiguration.Builder(projection)
                        .AddMatchingUsage(AudioUsageKind.Media)!.AddMatchingUsage(AudioUsageKind.Game)!.AddMatchingUsage(AudioUsageKind.Unknown)!.Build()!;
                    return new AudioRecord.Builder().SetAudioPlaybackCaptureConfig(capture)!;
                }, notes, "device audio");
                if (device is not null) deviceInput = Mixer.AddInput("device", 300);
            }
        }

        encoderFormat = MediaFormat.CreateAudioFormat(MediaFormat.MimetypeAudioAac, sampleRate, channels);
        encoderFormat.SetInteger(MediaFormat.KeyAacProfile, (int)MediaCodecProfileType.Aacobjectlc);
        encoderFormat.SetInteger(MediaFormat.KeyBitRate, settings.AudioBitrateKbps * 1000);
        encoderFormat.SetInteger(MediaFormat.KeyMaxInputSize, FramesPerBlock * channels * 2 * 2);
        try { encoder = CreateEncoder(); }
        catch (Exception ex) when (ex is Java.Lang.Exception)
        {
            throw new InvalidOperationException($"The AAC encoder rejected {sampleRate} Hz, {channels} channel(s), {settings.AudioBitrateKbps} kbps: {ex.Message}");
        }
        drain = new Thread(Drain) { IsBackground = true, Name = "AudioEncoder" };
        driver = new Thread(Drive) { IsBackground = true, Name = "AudioMixer", Priority = System.Threading.ThreadPriority.Highest };
        if (device is not null) deviceThread = new Thread(() => Capture(device, deviceInput!, isDevice: true)) { IsBackground = true, Name = "DeviceAudio" };
        if (microphone is not null) microphoneThread = new Thread(() => Capture(microphone, microphoneInput!)) { IsBackground = true, Name = "Microphone", Priority = System.Threading.ThreadPriority.Highest };
    }

    /// <summary>Start capture and output delivery only after format and frame consumers are attached.</summary>
    public void Start()
    {
        if (started) throw new InvalidOperationException("The audio pipeline is already running.");
        microphone?.StartRecording();
        device?.StartRecording();
        started = true;
        drain.Start(); driver.Start(); deviceThread?.Start(); microphoneThread?.Start();
    }

    private MediaCodec CreateEncoder()
    {
        var codec = MediaCodec.CreateEncoderByType(MediaFormat.MimetypeAudioAac);
        try { codec.Configure(encoderFormat, null, null, MediaCodecConfigFlags.Encode); codec.Start(); return codec; }
        catch { codec.Release(); throw; }
    }

    /// <summary>
    /// Replaces an encoder that left the running state (e.g. Codec2's watchdog after a stall under heavy load),
    /// so a transient failure costs a moment of audio instead of the stream.
    /// </summary>
    private MediaCodec Recover(MediaCodec failed, Exception error)
    {
        lock (codecLock)
        {
            if (encoder != failed) return encoder;
            if (++restarts > 10) throw new InvalidOperationException("The AAC encoder keeps failing.", error);
            Studio.Describe($"Restarting the AAC encoder (attempt {restarts})", error);
            try { failed.Release(); } catch { }
            encoder = CreateEncoder();
            return encoder;
        }
    }

    public AudioMixer Mixer { get; }
    public event Action<StreamAudioFormat>? FormatReady;
    public event Action<AudioFrame>? FrameReady;
    public string? Failure { get; private set; }
    /// <summary>PCM blocks given to the encoder and AAC frames it returned; for diagnostics.</summary>
    public long BlocksQueued, FramesEncoded;
    public bool HasFormat { get; private set; }

    private AudioRecord? CreateRecord(Func<AudioRecord.Builder> builder, List<string> notes, string name)
    {
        // Prefer the output format; fall back to mono, then 48 kHz. The mixer converts whatever the device delivers.
        foreach (var (rate, mask) in new[] { (sampleRate, channels == 2 ? ChannelIn.Stereo : ChannelIn.Mono), (sampleRate, ChannelIn.Mono), (48000, ChannelIn.Mono) }.Distinct())
        {
            try
            {
                var minimum = AudioRecord.GetMinBufferSize(rate, mask, Encoding.Pcm16bit);
                if (minimum <= 0) continue;
                var record = builder()
                    .SetAudioFormat(new AndroidAudioFormat.Builder().SetEncoding(Encoding.Pcm16bit)!.SetSampleRate(rate)!.SetChannelMask((global::Android.Media.ChannelOut)(int)mask)!.Build()!)!
                    .SetBufferSizeInBytes(Math.Max(minimum * 2, FramesPerBlock * 2 * 2 * 4))!.Build()!;
                if (record.State == State.Initialized) return record;
                record.Release();
            }
            catch (Exception ex) when (ex is Java.Lang.Exception) { }
        }
        notes.Add($"The {name} could not be opened; streaming without it.");
        return null;
    }

    /// <summary>
    /// Mixes on the monotonic clock so the stream never waits for a capture device; each source (including the
    /// microphone) fills its own bounded buffer from its own thread.
    /// </summary>
    private void Drive()
    {
        var block = new short[FramesPerBlock * channels];
        long blocks = 0;
        try
        {
            var start = Now();
            while (running)
            {
                var due = start + blocks * FramesPerBlock * 1_000_000_000L / sampleRate;
                var wait = due - Now();
                if (wait > 0) Thread.Sleep(TimeSpan.FromTicks(wait / 100));
                else if (-wait > 500_000_000L) { start = due = Now(); blocks = 0; } // the device stalled: restart the clock
                Mixer.Mix(block);
                // The timestamp is when the block's audio was captured: the sources are buffered by about one block.
                Encode(block, due - FramesPerBlock * 1_000_000_000L / sampleRate);
                blocks++;
            }
        }
        catch (Exception ex) when (running) { Failure = Studio.Describe("Audio mixing stopped", ex); }
    }

    private void Capture(AudioRecord record, AudioMixerInput input, bool isDevice = false)
    {
        var buffer = new short[FramesPerBlock * record.ChannelCount];
        try
        {
            while (running && (!isDevice || deviceCapturing))
            {
                var read = record.Read(buffer, 0, buffer.Length);
                if (read <= 0) { if (!running) break; Thread.Sleep(10); continue; }
                input.Write(buffer.AsSpan(0, read), record.SampleRate, record.ChannelCount);
            }
        }
        catch (Exception) when (!running || (isDevice && !deviceCapturing)) { }
        catch (Exception ex) { Studio.Describe("Audio capture stopped", ex); }
    }

    private void Encode(short[] block, long timeNs)
    {
        var ptsUs = (timeNs - originNs) / 1000;
        if (ptsUs < 0) return;
        // Queue in pieces no larger than the codec's input buffers, advancing the timestamp per sample.
        var codec = encoder;
        try { Queue(codec, block, ptsUs); Interlocked.Increment(ref BlocksQueued); }
        catch (Java.Lang.IllegalStateException ex) when (running) { Recover(codec, ex); }
    }

    private void Queue(MediaCodec encoder, short[] block, long ptsUs)
    {
        var offset = 0;
        while (offset < block.Length)
        {
            var index = encoder.DequeueInputBuffer(50_000);
            if (index < 0) return;
            using var buffer = encoder.GetInputBuffer(index)!;
            var samples = Math.Min(block.Length - offset, buffer.Capacity() / 2 / channels * channels);
            if (samples <= 0) { encoder.QueueInputBuffer(index, 0, 0, ptsUs, MediaCodecBufferFlags.None); return; }
            Marshal.Copy(block, offset, buffer.GetDirectBufferAddress(), samples);
            encoder.QueueInputBuffer(index, 0, samples * 2, ptsUs + offset / channels * 1_000_000L / sampleRate, MediaCodecBufferFlags.None);
            offset += samples;
        }
    }

    private void Drain()
    {
        var info = new MediaCodec.BufferInfo();
        try
        {
            while (running)
            {
                var codec = encoder;
                try { DrainOnce(codec, info); }
                catch (Java.Lang.IllegalStateException ex) when (running) { Recover(codec, ex); }
            }
        }
        catch (Exception ex) when (running) { Failure = Studio.Describe("Audio encoder stopped", ex); }
        catch { }
    }

    private void DrainOnce(MediaCodec codec, MediaCodec.BufferInfo info)
    {
        var index = codec.DequeueOutputBuffer(info, 20_000);
        if (index == (int)MediaCodecInfoState.OutputFormatChanged)
        {
            using var format = codec.OutputFormat;
            if (format.GetByteBuffer("csd-0") is { } csd)
            {
                using var _ = csd;
                var config = new byte[csd.Remaining()];
                csd.Get(config);
                HasFormat = true;
                FormatReady?.Invoke(StreamAudioFormat.FromAudioSpecificConfig(config));
            }
            return;
        }
        if (index < 0) return;
        var data = new byte[info.Size];
        if (info.Size > 0) { using var buffer = codec.GetOutputBuffer(index)!; Marshal.Copy(buffer.GetDirectBufferAddress() + info.Offset, data, 0, info.Size); }
        codec.ReleaseOutputBuffer(index, false);
        if ((info.Flags & MediaCodecBufferFlags.CodecConfig) != 0) { HasFormat = true; FormatReady?.Invoke(StreamAudioFormat.FromAudioSpecificConfig(data)); return; }
        if (data.Length > 0) { Interlocked.Increment(ref FramesEncoded); FrameReady?.Invoke(new AudioFrame(data, info.PresentationTimeUs)); }
    }

    private static long Now() => Stopwatch.GetTimestamp() * (1_000_000_000 / Stopwatch.Frequency);

    public void StopDeviceCapture()
    {
        deviceCapturing = false;
        try { device?.Stop(); } catch { }
        if (started) deviceThread?.Join(1000);
        try { device?.Release(); } catch { }
        device = null;
    }

    public void Dispose()
    {
        running = false;
        try { microphone?.Stop(); } catch { }
        try { device?.Stop(); } catch { }
        if (started) { driver.Join(1000); drain.Join(1000); deviceThread?.Join(1000); microphoneThread?.Join(1000); }
        microphone?.Release(); device?.Release();
        try { encoder.Stop(); } catch { }
        try { encoder.Release(); } catch { }
    }
}
