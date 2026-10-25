using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using DeveLiveStreamer.Streaming.Ingest;
using DeveLiveStreamer.Streaming.Media;
using DeveLiveStreamer.Streaming.MpegTs;
using DeveLiveStreamer.Streaming.Publishing;
using Xunit.Abstractions;

namespace DeveLiveStreamer.Streaming.Tests;

/// <summary>
/// Exchanges real streams with FFmpeg over loopback SRT and RTMP. These run when ffmpeg/ffprobe (with libsrt,
/// libx264 and libx265) are installed and are reported as trivially passing otherwise.
/// </summary>
public sealed class InteropTests(InteropTests.Media media, ITestOutputHelper output) : IClassFixture<InteropTests.Media>
{
    [Theory]
    [InlineData(VideoCodec.H264)]
    [InlineData(VideoCodec.H265)]
    public void MuxedTransportStreamDecodesCleanlyInFfmpeg(VideoCodec codec)
    {
        if (!media.Available) { output.WriteLine("Skipped: " + media.UnavailableReason); return; }
        var clip = media.Clip(codec);
        var muxer = new TsMuxer(clip.Video, clip.Audio);
        var path = media.TempFile(".ts");
        using (var file = File.Create(path))
            foreach (var item in clip.Interleaved())
                file.Write(item is VideoFrame v ? muxer.WriteVideo(v) : muxer.WriteAudio((AudioFrame)item));
        var probe = Probe(path);
        Assert.Equal(clip.Frames.Count, probe.VideoFrames);
        Assert.Equal(clip.Samples.Count, probe.AudioFrames);
        Assert.Equal(codec == VideoCodec.H264 ? "h264" : "hevc", probe.VideoCodec);
        Assert.Equal(string.Empty, Ffmpeg($"-v error -xerror -i {path} -f null -").Trim());
        output.WriteLine($"{codec}: {probe.VideoFrames} video and {probe.AudioFrames} audio packets decoded without errors");
    }

    [Theory]
    [InlineData(VideoCodec.H264)]
    [InlineData(VideoCodec.H265)]
    public async Task SrtPublishingReachesAnFfmpegListener(VideoCodec codec)
    {
        if (!media.Available) { output.WriteLine("Skipped: " + media.UnavailableReason); return; }
        var port = FreeUdpPort();
        var path = media.TempFile(".ts");
        using var receiver = StartFfmpeg($"-v error -y -i srt://127.0.0.1:{port}?mode=listener -c copy -f mpegts {path}");
        await Task.Delay(700);
        await PublishAsync(StreamEndpoint.Parse($"srt://127.0.0.1:{port}?streamid=default/app/test"), media.Clip(codec));
        await WaitForExit(receiver);
        var probe = Probe(path);
        Assert.Equal(media.Clip(codec).Frames.Count, probe.VideoFrames);
        Assert.InRange(probe.AudioFrames, media.Clip(codec).Samples.Count - 3, media.Clip(codec).Samples.Count);
    }

    [Theory]
    [InlineData(VideoCodec.H264)]
    [InlineData(VideoCodec.H265)]
    public async Task RtmpPublishingReachesAnFfmpegServer(VideoCodec codec)
    {
        if (!media.Available) { output.WriteLine("Skipped: " + media.UnavailableReason); return; }
        var port = FreeTcpPort();
        var path = media.TempFile(".flv");
        using var receiver = StartFfmpeg($"-v error -y -listen 1 -i rtmp://127.0.0.1:{port}/live/test -c copy -f flv {path}");
        await Task.Delay(700);
        await PublishAsync(StreamEndpoint.Parse($"rtmp://127.0.0.1:{port}/live", "test"), media.Clip(codec));
        await WaitForExit(receiver);
        var probe = Probe(path);
        Assert.Equal(codec == VideoCodec.H264 ? "h264" : "hevc", probe.VideoCodec);
        Assert.Equal(media.Clip(codec).Frames.Count, probe.VideoFrames);
        Assert.InRange(probe.AudioFrames, media.Clip(codec).Samples.Count - 3, media.Clip(codec).Samples.Count);
    }

    [Fact]
    public async Task SrtListenerReceivesStreamIdAndFramesFromOurPublisher()
    {
        if (!media.Available) { output.WriteLine("Skipped: " + media.UnavailableReason); return; }
        var port = FreeUdpPort();
        await using var incoming = IncomingStream.Create($"srt://0.0.0.0:{port}");
        var received = Collect(incoming);
        var states = new List<string>();
        incoming.StateChanged += (_, text) => { lock (states) states.Add(text); };
        incoming.Start();
        await Task.Delay(300);
        await PublishAsync(StreamEndpoint.Parse($"srt://127.0.0.1:{port}?streamid=default/app/route-check"), media.Clip(VideoCodec.H264));
        await WaitUntil(() => received.Frames.Count >= media.Clip(VideoCodec.H264).Frames.Count - 1);
        lock (states) Assert.Contains(states, text => text.Contains("default/app/route-check"));
        Assert.Equal((640, 360), (received.Format!.Width, received.Format.Height));
        Assert.True(received.Frames[0].IsKeyFrame);
    }

    [Theory]
    [InlineData("rtmp")]
    [InlineData("srt")]
    public async Task FfmpegCanPushIntoOurListeningSources(string protocol)
    {
        if (!media.Available) { output.WriteLine("Skipped: " + media.UnavailableReason); return; }
        var port = protocol == "srt" ? FreeUdpPort() : FreeTcpPort();
        await using var incoming = IncomingStream.Create(protocol == "srt" ? $"srt://0.0.0.0:{port}" : $"rtmp://0.0.0.0:{port}/live/key");
        var received = Collect(incoming);
        incoming.Start();
        await Task.Delay(300);
        var target = protocol == "srt" ? $"-f mpegts srt://127.0.0.1:{port}?streamid=pushed" : $"-f flv rtmp://127.0.0.1:{port}/live/key";
        using var sender = StartFfmpeg($"-v error -re -i {media.Clip(VideoCodec.H264).Path} -c copy {target}");
        await WaitUntil(() => received.Frames.Count >= 30 && received.Audio.Count >= 30);
        Assert.Equal(IncomingState.Receiving, incoming.State);
        Assert.Equal((640, 360), (received.Format!.Width, received.Format.Height));
        Assert.Equal(48000, received.AudioFormat!.SampleRate);
        Assert.True(received.Frames[0].IsKeyFrame);
    }

    [Fact]
    public async Task RtmpListenerRejectsAWrongStreamKey()
    {
        if (!media.Available) { output.WriteLine("Skipped: " + media.UnavailableReason); return; }
        var port = FreeTcpPort();
        await using var incoming = IncomingStream.Create($"rtmp://0.0.0.0:{port}/live/secret");
        var received = Collect(incoming);
        incoming.Start();
        await Task.Delay(300);
        using var sender = StartFfmpeg($"-v error -re -i {media.Clip(VideoCodec.H264).Path} -c copy -f flv rtmp://127.0.0.1:{port}/live/wrong");
        await WaitForExit(sender);
        Assert.Empty(received.Frames);
        Assert.NotEqual(0, sender.ExitCode);
    }

    [Fact]
    public async Task RtmpPullSourcePlaysFromAnFfmpegServer()
    {
        if (!media.Available) { output.WriteLine("Skipped: " + media.UnavailableReason); return; }
        var port = FreeTcpPort();
        using var server = StartFfmpeg($"-v error -re -stream_loop 3 -i {media.Clip(VideoCodec.H264).Path} -c copy -f flv -listen 1 rtmp://127.0.0.1:{port}/live/feed");
        await Task.Delay(800);
        await using var incoming = IncomingStream.Create($"rtmp://127.0.0.1:{port}/live/feed");
        var received = Collect(incoming);
        incoming.Start();
        await WaitUntil(() => received.Frames.Count >= 30);
        Assert.Equal((640, 360), (received.Format!.Width, received.Format.Height));
        Assert.NotNull(received.AudioFormat);
    }

    /// <summary>
    /// Streams the clip in real time through our publisher to a real server, e.g. an OvenMediaEngine deployment:
    /// DLS_PUBLISH_URL=srt://host:9999?streamid=default/app/name (optional DLS_PUBLISH_SECONDS, default 20).
    /// </summary>
    [Fact]
    public async Task PublishesInRealTimeToAConfiguredServer()
    {
        var url = Environment.GetEnvironmentVariable("DLS_PUBLISH_URL");
        if (string.IsNullOrEmpty(url) || !media.Available) { output.WriteLine("Skipped: set DLS_PUBLISH_URL to publish to a server."); return; }
        var seconds = int.TryParse(Environment.GetEnvironmentVariable("DLS_PUBLISH_SECONDS"), out var s) ? s : 20;
        var clip = media.Clip(VideoCodec.H264);
        await using var publisher = new LivePublisher(new PublishOptions(StreamEndpoint.Parse(url), true, true, 0, 2000, 128));
        publisher.SetVideoFormat(clip.Video);
        publisher.SetAudioFormat(clip.Audio);
        publisher.StateChanged += (state, message) => output.WriteLine($"{state} {message}");
        publisher.Start();
        await WaitUntil(() => publisher.State == PublisherState.Live);
        const long ClipUs = 2_000_000;
        var start = Stopwatch.GetTimestamp();
        for (var loop = 0L; loop * ClipUs < seconds * 1_000_000L; loop++)
            foreach (var item in clip.Interleaved())
            {
                var at = (item is VideoFrame v ? v.DtsUs : ((AudioFrame)item).PtsUs) + loop * ClipUs;
                var wait = TimeSpan.FromMicroseconds(at) - Stopwatch.GetElapsedTime(start);
                if (wait > TimeSpan.Zero) await Task.Delay(wait);
                if (item is VideoFrame video) publisher.Push(video with { PtsUs = video.PtsUs + loop * ClipUs, DtsUs = video.DtsUs + loop * ClipUs });
                else { var audio = (AudioFrame)item; publisher.Push(audio with { PtsUs = audio.PtsUs + loop * ClipUs }); }
            }
        var stats = publisher.Statistics;
        output.WriteLine($"{stats.State}: sent {stats.VideoFramesSent} frames, {stats.BytesSent} bytes, dropped {stats.DroppedFrames}, " +
            $"reconnects {stats.Reconnects}, SRT RTT {stats.Srt?.RttMs} ms, lost {stats.Srt?.PacketsLost}, retransmitted {stats.Srt?.PacketsRetransmitted}");
        Assert.Equal(PublisherState.Live, stats.State);
        Assert.Equal(0, stats.Reconnects);
    }

    private async Task PublishAsync(StreamEndpoint endpoint, Clip clip)
    {
        await using var publisher = new LivePublisher(new PublishOptions(endpoint, true, true, 120, 2000, 128));
        publisher.SetVideoFormat(clip.Video);
        publisher.SetAudioFormat(clip.Audio);
        var live = new TaskCompletionSource();
        publisher.StateChanged += (state, message) =>
        {
            output.WriteLine($"{state} {message}");
            if (state == PublisherState.Live) live.TrySetResult();
        };
        publisher.Start();
        await live.Task.WaitAsync(TimeSpan.FromSeconds(10));
        // Pace at roughly 4x real time so loopback receivers are not flooded.
        var start = Stopwatch.GetTimestamp();
        foreach (var item in clip.Interleaved())
        {
            var at = item is VideoFrame v ? v.DtsUs : ((AudioFrame)item).PtsUs;
            var wait = TimeSpan.FromMicroseconds(at / 4) - Stopwatch.GetElapsedTime(start);
            if (wait > TimeSpan.Zero) await Task.Delay(wait);
            if (item is VideoFrame video) publisher.Push(video); else publisher.Push((AudioFrame)item);
        }
        await WaitUntil(() => publisher.Statistics.VideoFramesSent >= clip.Frames.Count);
        await Task.Delay(300);
        var stats = publisher.Statistics;
        output.WriteLine($"sent {stats.VideoFramesSent} frames, {stats.BytesSent} bytes, dropped {stats.DroppedFrames}, SRT RTT {stats.Srt?.RttMs}");
        Assert.Equal(0, stats.DroppedFrames);
    }

    private static Received Collect(IncomingStream incoming)
    {
        var received = new Received();
        incoming.VideoFormatChanged += f => received.Format = f;
        incoming.AudioFormatChanged += f => received.AudioFormat = f;
        incoming.VideoFrameReceived += f => { lock (received.Frames) received.Frames.Add(f); };
        incoming.AudioFrameReceived += f => { lock (received.Audio) received.Audio.Add(f); };
        return received;
    }

    private sealed class Received
    {
        public VideoFormat? Format;
        public AudioFormat? AudioFormat;
        public readonly List<VideoFrame> Frames = [];
        public readonly List<AudioFrame> Audio = [];
    }

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 15000)
    {
        var start = Stopwatch.GetTimestamp();
        while (!condition())
        {
            if (Stopwatch.GetElapsedTime(start).TotalMilliseconds > timeoutMs) throw new TimeoutException("Condition not reached.");
            await Task.Delay(50);
        }
    }

    private static async Task WaitForExit(Process process)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await process.WaitForExitAsync(timeout.Token);
    }

    private static Process StartFfmpeg(string arguments) => Process.Start(new ProcessStartInfo("ffmpeg", "-hide_banner -nostdin " + arguments)
    { RedirectStandardError = true, UseShellExecute = false })!;

    /// <summary>Runs ffmpeg and returns its combined output.</summary>
    internal static string Ffmpeg(string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo("ffmpeg", "-hide_banner -nostdin " + arguments)
        { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false })!;
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return error + standardOutput.Result;
    }

    private static (int VideoFrames, int AudioFrames, string VideoCodec) Probe(string path)
    {
        using var process = Process.Start(new ProcessStartInfo("ffprobe", $"-v error -count_packets -show_entries stream=codec_type,codec_name,nb_read_packets -of json {path}")
        { RedirectStandardOutput = true, UseShellExecute = false })!;
        var json = JsonDocument.Parse(process.StandardOutput.ReadToEnd());
        process.WaitForExit();
        int video = 0, audio = 0; var codec = "";
        foreach (var stream in json.RootElement.GetProperty("streams").EnumerateArray())
        {
            var count = int.Parse(stream.GetProperty("nb_read_packets").GetString()!);
            if (stream.GetProperty("codec_type").GetString() == "video") { video = count; codec = stream.GetProperty("codec_name").GetString()!; }
            else audio = count;
        }
        return (video, audio, codec);
    }

    private static int FreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port;
    }

    private static int FreeUdpPort()
    {
        using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
    }

    public sealed record Clip(string Path, VideoFormat Video, AudioFormat Audio, List<VideoFrame> Frames, List<AudioFrame> Samples)
    {
        public IEnumerable<object> Interleaved() =>
            Frames.Select(f => (Time: f.DtsUs, Item: (object)f)).Concat(Samples.Select(s => (Time: s.PtsUs, Item: (object)s)))
                .OrderBy(x => x.Time).Select(x => x.Item);
    }

    /// <summary>Generates 2-second 640x360 H.264/HEVC + AAC clips once and demuxes them with our TS demuxer.</summary>
    public sealed class Media : IDisposable
    {
        private readonly string directory = Directory.CreateTempSubdirectory("dls-interop-").FullName;
        private readonly Dictionary<VideoCodec, Clip> clips = [];
        public bool Available { get; }
        public string? UnavailableReason { get; }

        public Media()
        {
            try
            {
                foreach (var (codec, encoder) in new[] { (VideoCodec.H264, "libx264 -profile:v high -bf 0"), (VideoCodec.H265, "libx265 -x265-params log-level=error:bframes=0") })
                {
                    var path = Path.Combine(directory, $"{codec}.ts");
                    var error = Ffmpeg($"-v error -y -f lavfi -i testsrc2=size=640x360:rate=30 -f lavfi -i sine=frequency=440:sample_rate=48000 -t 2 " +
                        $"-c:v {encoder} -g 30 -pix_fmt yuv420p -c:a aac -ac 2 -b:a 128k -f mpegts {path}");
                    if (!File.Exists(path)) throw new InvalidOperationException(error);
                    clips[codec] = Demux(path, codec);
                }
                Available = Ffmpeg("-protocols").Contains("srt");
                if (!Available) UnavailableReason = "ffmpeg was built without SRT support";
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { UnavailableReason = ex.Message; }
        }

        public Clip Clip(VideoCodec codec) => clips[codec];
        public string TempFile(string extension) => Path.Combine(directory, Guid.NewGuid().ToString("N") + extension);

        private static Clip Demux(string path, VideoCodec codec)
        {
            var demuxer = new TsDemuxer();
            VideoFormat? video = null; AudioFormat? audio = null;
            var frames = new List<VideoFrame>(); var samples = new List<AudioFrame>();
            demuxer.VideoFormatChanged += f => video = f;
            demuxer.AudioFormatChanged += f => audio = f;
            demuxer.VideoFrameReceived += frames.Add;
            demuxer.AudioFrameReceived += samples.Add;
            demuxer.Push(File.ReadAllBytes(path));
            demuxer.Flush();
            if (video?.Codec != codec) throw new InvalidOperationException("Unexpected codec in generated clip.");
            // Rebase timestamps to zero like an encoder session clock.
            var origin = Math.Min(frames[0].DtsUs, samples[0].PtsUs);
            return new(path, video with { FrameRate = 30 }, audio!,
                frames.Select(f => f with { PtsUs = f.PtsUs - origin, DtsUs = f.DtsUs - origin }).ToList(),
                samples.Select(s => s with { PtsUs = s.PtsUs - origin }).ToList());
        }

        public void Dispose() { try { Directory.Delete(directory, true); } catch { } }
    }
}
