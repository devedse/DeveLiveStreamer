using System.Diagnostics;
using DeveLiveStreamer.Streaming.Media;
using DeveLiveStreamer.Streaming.Publishing;
using DeveLiveStreamer.Streaming.Srt;

namespace DeveLiveStreamer.Streaming.Tests;

public sealed class PublisherTests
{
    private static readonly VideoFormat Video = VideoFormat.FromParameterSets(VideoCodec.H264, [FormatTests.Sps, FormatTests.Pps], 30);
    private static readonly AudioFormat Audio = AudioFormat.Aac(48000, 2);
    private static PublishOptions Options => new(StreamEndpoint.Parse("srt://127.0.0.1:9?streamid=a/b/c"), true, true, 0, 1000, 128)
    { ConnectTimeout = TimeSpan.FromSeconds(2) };

    [Fact]
    public async Task ConnectsOnlyOnceFormatsAreKnownAndStartsAtAKeyFrame()
    {
        var sinks = new List<FakeSink>();
        await using var publisher = new LivePublisher(Options) { SinkFactory = _ => { var s = new FakeSink(); lock (sinks) sinks.Add(s); return Task.FromResult<IMediaSink>(s); } };
        var keyRequests = 0;
        publisher.KeyFrameRequested += () => Interlocked.Increment(ref keyRequests);
        publisher.Start();
        await Task.Delay(150);
        Assert.Empty(sinks);
        publisher.SetVideoFormat(Video);
        publisher.SetAudioFormat(Audio);
        await WaitUntil(() => publisher.State == PublisherState.Live);
        Assert.True(keyRequests >= 1);
        publisher.Push(Frame(0, key: false));
        publisher.Push(new AudioFrame([1], 0));
        publisher.Push(Frame(1, key: true));
        publisher.Push(new AudioFrame([2], 20_000));
        publisher.Push(Frame(2, key: false));
        await WaitUntil(() => sinks[0].Video.Count == 2);
        Assert.Equal([1, 2], sinks[0].Video.Select(f => f.PtsUs / 33_333));
        // Audio before the first key frame is held back so receivers start with aligned tracks.
        Assert.Equal([20_000L], sinks[0].Audio.Select(a => a.PtsUs));
        Assert.Equal(1, publisher.Statistics.DroppedFrames);
    }

    [Fact]
    public async Task ReconnectsAfterATransportFailureAndResumesAtTheNextKeyFrame()
    {
        var sinks = new List<FakeSink>();
        await using var publisher = new LivePublisher(Options) { SinkFactory = _ => { var s = new FakeSink(); lock (sinks) sinks.Add(s); return Task.FromResult<IMediaSink>(s); } };
        var states = new List<PublisherState>();
        publisher.StateChanged += (state, _) => { lock (states) states.Add(state); };
        publisher.SetVideoFormat(Video); publisher.SetAudioFormat(Audio);
        publisher.Start();
        await WaitUntil(() => publisher.State == PublisherState.Live);
        publisher.Push(Frame(0, key: true));
        await WaitUntil(() => sinks[0].Video.Count == 1);
        sinks[0].Fail = true;
        publisher.Push(Frame(1, key: false));
        await WaitUntil(() => sinks.Count == 2 && publisher.State == PublisherState.Live);
        publisher.Push(Frame(2, key: false));
        publisher.Push(Frame(3, key: true));
        await WaitUntil(() => sinks[1].Video.Count == 1);
        Assert.True(sinks[1].Video[0].IsKeyFrame);
        Assert.Contains(PublisherState.Reconnecting, states);
        Assert.Equal(1, publisher.Statistics.Reconnects);
        Assert.True(sinks[0].Disposed);
    }

    [Fact]
    public async Task ConnectionErrorsAreReportedAndRetried()
    {
        var attempts = 0;
        await using var publisher = new LivePublisher(Options)
        {
            SinkFactory = _ => Interlocked.Increment(ref attempts) < 2 ? throw new IOException("refused") : Task.FromResult<IMediaSink>(new FakeSink())
        };
        string? message = null;
        publisher.StateChanged += (state, text) => { if (state == PublisherState.Reconnecting) message = text; };
        publisher.SetVideoFormat(Video); publisher.SetAudioFormat(Audio);
        publisher.Start();
        await WaitUntil(() => publisher.State == PublisherState.Live, 5000);
        Assert.Equal("refused", message);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task ContinuesWithVideoOnlyWhenAudioIsAbandoned()
    {
        var sink = new FakeSink();
        await using var publisher = new LivePublisher(Options) { SinkFactory = _ => Task.FromResult<IMediaSink>(sink) };
        publisher.SetVideoFormat(Video);
        publisher.Start();
        await Task.Delay(100);
        Assert.NotEqual(PublisherState.Live, publisher.State);
        Assert.True(publisher.AbandonAudio());
        await WaitUntil(() => publisher.State == PublisherState.Live);
        publisher.Push(Frame(0, key: true));
        publisher.Push(new AudioFrame([1], 0));
        await WaitUntil(() => sink.Video.Count == 1);
        Assert.Empty(sink.Audio);
    }

    private static VideoFrame Frame(int index, bool key) => new([0, 0, 0, 1, key ? (byte)0x65 : (byte)0x41, 1], index * 33_333, index * 33_333, key);

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 3000)
    {
        var start = Stopwatch.GetTimestamp();
        while (!condition())
        {
            if (Stopwatch.GetElapsedTime(start).TotalMilliseconds > timeoutMs) throw new TimeoutException();
            await Task.Delay(10);
        }
    }

    private sealed class FakeSink : IMediaSink
    {
        public readonly List<VideoFrame> Video = [];
        public readonly List<AudioFrame> Audio = [];
        public volatile bool Fail, Disposed;
        public long BytesSent { get; private set; }
        public SrtStatistics? SrtStatistics => null;
        public void WriteVideo(VideoFrame frame) { if (Fail) throw new IOException("broken pipe"); Video.Add(frame); BytesSent += frame.Data.Length; }
        public void WriteAudio(AudioFrame frame) { if (Fail) throw new IOException("broken pipe"); Audio.Add(frame); }
        public void Dispose() => Disposed = true;
    }
}
