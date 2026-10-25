using System.Diagnostics;
using System.Threading.Channels;
using DeveLiveStreamer.Streaming.Media;
using DeveLiveStreamer.Streaming.Srt;

namespace DeveLiveStreamer.Streaming.Publishing;

public enum PublisherState { Idle, Connecting, Live, Reconnecting, Stopped }

public sealed record PublisherStatistics(PublisherState State, TimeSpan LiveDuration, double BitrateKbps, long BytesSent,
    long VideoFramesSent, long DroppedFrames, int Reconnects, SrtStatistics? Srt, string? LastError);

public sealed record PublishOptions(StreamEndpoint Endpoint, bool HasVideo, bool HasAudio, int SrtLatencyMs, int VideoBitrateKbps, int AudioBitrateKbps)
{
    /// <summary>Frames waiting longer than this are discarded (video until the next key frame) to bound delay.</summary>
    public TimeSpan MaximumQueueDelay { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>
/// Sends encoded frames to one endpoint on a background task. Connects once the formats are known,
/// reconnects with back-off after failures, and starts every connection at a key frame.
/// </summary>
public sealed class LivePublisher : IAsyncDisposable
{
    private readonly PublishOptions options;
    private readonly Channel<(object Frame, long Enqueued)> queue = Channel.CreateUnbounded<(object, long)>(new() { SingleReader = true });
    private readonly CancellationTokenSource stopping = new();
    private readonly object statsLock = new();
    private VideoFormat? videoFormat;
    private AudioFormat? audioFormat;
    private Task? worker;
    private IMediaSink? sink;
    private PublisherState state;
    private long liveSince, videoFramesSent, droppedFrames, lastBytes, lastRateTicks, totalBytes;
    private double bitrateKbps;
    private int reconnects;
    private string? lastError;
    private volatile bool audioAbandoned;

    public LivePublisher(PublishOptions options) => this.options = options;

    public event Action<PublisherState, string?>? StateChanged;
    /// <summary>Raised from the publishing task when the encoder should produce a key frame soon.</summary>
    public event Action? KeyFrameRequested;
    public Func<CancellationToken, Task<IMediaSink>>? SinkFactory { get; set; }
    public PublisherState State => state;

    private bool HasAudio => options.HasAudio && !audioAbandoned;

    /// <summary>
    /// Continues without an audio track when audio failed before producing its format, rather than never connecting.
    /// Has no effect once audio is flowing.
    /// </summary>
    public bool AbandonAudio()
    {
        if (Volatile.Read(ref audioFormat) is not null) return false;
        audioAbandoned = true;
        return true;
    }

    public void SetVideoFormat(VideoFormat format) => Volatile.Write(ref videoFormat, format);
    public void SetAudioFormat(AudioFormat format) => Volatile.Write(ref audioFormat, format);

    public void Start()
    {
        if (worker is not null) throw new InvalidOperationException("The publisher is already running.");
        worker = Task.Run(RunAsync);
    }

    public void Push(VideoFrame frame) { if (options.HasVideo) queue.Writer.TryWrite((frame, Stopwatch.GetTimestamp())); }
    public void Push(AudioFrame frame) { if (HasAudio) queue.Writer.TryWrite((frame, Stopwatch.GetTimestamp())); }

    public PublisherStatistics Statistics
    {
        get
        {
            lock (statsLock)
            {
                var current = sink;
                var bytes = totalBytes + (current?.BytesSent ?? 0);
                var now = Stopwatch.GetTimestamp();
                var elapsed = Stopwatch.GetElapsedTime(lastRateTicks, now).TotalSeconds;
                if (lastRateTicks == 0) { lastRateTicks = now; lastBytes = bytes; }
                else if (elapsed >= 0.5)
                {
                    bitrateKbps = Math.Max(0, bytes - lastBytes) * 8 / 1000d / elapsed;
                    lastBytes = bytes; lastRateTicks = now;
                }
                SrtStatistics? srt = null;
                try { srt = current?.SrtStatistics; } catch (ObjectDisposedException) { }
                var live = state == PublisherState.Live ? Stopwatch.GetElapsedTime(liveSince) : TimeSpan.Zero;
                return new(state, live, state == PublisherState.Live ? bitrateKbps : 0, bytes, Interlocked.Read(ref videoFramesSent),
                    Interlocked.Read(ref droppedFrames), reconnects, srt, lastError);
            }
        }
    }

    private async Task RunAsync()
    {
        var token = stopping.Token;
        var attempt = 0;
        var wasLive = false;
        while (!token.IsCancellationRequested)
        {
            if ((options.HasVideo && Volatile.Read(ref videoFormat) is null) || (HasAudio && Volatile.Read(ref audioFormat) is null))
            {
                Discard();
                try { await Task.Delay(20, token); } catch (OperationCanceledException) { break; }
                continue;
            }
            SetState(wasLive || attempt > 0 ? PublisherState.Reconnecting : PublisherState.Connecting, lastError);
            IMediaSink connected;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(options.ConnectTimeout);
                connected = await (SinkFactory?.Invoke(timeout.Token) ?? ConnectAsync(timeout.Token));
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                lastError = ex is OperationCanceledException ? "Connection timed out." : ex.Message;
                attempt++;
                SetState(PublisherState.Reconnecting, lastError);
                try { await Task.Delay(TimeSpan.FromSeconds(Math.Min(10, Math.Pow(2, Math.Min(attempt, 4)) / 2)), token); } catch (OperationCanceledException) { }
                continue;
            }
            catch (OperationCanceledException) { break; }

            if (wasLive) reconnects++;
            attempt = 0; wasLive = true; lastError = null;
            lock (statsLock) { sink = connected; liveSince = Stopwatch.GetTimestamp(); }
            // Stale frames from before the connection are not worth sending; start at a fresh key frame.
            Discard();
            KeyFrameRequested?.Invoke();
            SetState(PublisherState.Live, null);
            try { await SendAsync(connected, token); }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                lastError = ex.Message;
                SetState(PublisherState.Reconnecting, lastError);
            }
            catch (OperationCanceledException) { }
            finally
            {
                lock (statsLock) { totalBytes += connected.BytesSent; sink = null; }
                try { connected.Dispose(); } catch { }
            }
            if (!token.IsCancellationRequested) try { await Task.Delay(500, token); } catch (OperationCanceledException) { }
        }
        SetState(PublisherState.Stopped, lastError);
    }

    private async Task<IMediaSink> ConnectAsync(CancellationToken cancellation) => options.Endpoint switch
    {
        SrtEndpoint srt => await SrtPublishSink.ConnectAsync(srt, Format(videoFormat), HasAudio ? audioFormat : null, options.SrtLatencyMs, cancellation),
        RtmpEndpoint rtmp => await RtmpPublishSink.ConnectAsync(rtmp, Format(videoFormat), HasAudio ? audioFormat : null, options.VideoBitrateKbps, options.AudioBitrateKbps, cancellation),
        _ => throw new NotSupportedException("Unsupported endpoint.")
    };

    private VideoFormat? Format(VideoFormat? format) => options.HasVideo ? format : null;

    private async Task SendAsync(IMediaSink output, CancellationToken token)
    {
        var waitingForKey = options.HasVideo;
        var maximumDelay = options.MaximumQueueDelay;
        while (await queue.Reader.WaitToReadAsync(token))
        {
            while (queue.Reader.TryRead(out var item))
            {
                var late = Stopwatch.GetElapsedTime(item.Enqueued) > maximumDelay;
                if (item.Frame is VideoFrame video)
                {
                    if (late && !video.IsKeyFrame)
                    {
                        if (!waitingForKey) KeyFrameRequested?.Invoke();
                        waitingForKey = true;
                    }
                    if (waitingForKey && !video.IsKeyFrame) { Interlocked.Increment(ref droppedFrames); continue; }
                    waitingForKey = false;
                    output.WriteVideo(video);
                    Interlocked.Increment(ref videoFramesSent);
                }
                else if (item.Frame is AudioFrame audio)
                {
                    // Audio waits for the first key frame so receivers start with aligned tracks.
                    if (late || (waitingForKey && options.HasVideo)) continue;
                    output.WriteAudio(audio);
                }
            }
        }
    }

    private void Discard()
    {
        while (queue.Reader.TryRead(out var item)) if (item.Frame is VideoFrame) Interlocked.Increment(ref droppedFrames);
    }

    private void SetState(PublisherState value, string? message)
    {
        state = value;
        try { StateChanged?.Invoke(value, message); } catch { }
    }

    public async Task StopAsync()
    {
        if (stopping.IsCancellationRequested) return;
        stopping.Cancel();
        queue.Writer.TryComplete();
        lock (statsLock) try { sink?.Dispose(); } catch { }
        if (worker is not null) try { await worker; } catch { }
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
