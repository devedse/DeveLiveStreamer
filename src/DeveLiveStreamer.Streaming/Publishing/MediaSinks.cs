using System.Net;
using DeveLiveStreamer.Streaming.Flv;
using DeveLiveStreamer.Streaming.Media;
using DeveLiveStreamer.Streaming.MpegTs;
using DeveLiveStreamer.Streaming.Rtmp;
using DeveLiveStreamer.Streaming.Srt;

namespace DeveLiveStreamer.Streaming.Publishing;

/// <summary>A connected output. Writes block while the transport applies back-pressure and throw when it fails.</summary>
public interface IMediaSink : IDisposable
{
    void WriteVideo(VideoFrame frame);
    void WriteAudio(AudioFrame frame);
    long BytesSent { get; }
    SrtStatistics? SrtStatistics { get; }
}

public sealed class SrtPublishSink : IMediaSink
{
    private readonly SrtSocket socket;
    private readonly TsMuxer muxer;
    private long bytesSent;

    private SrtPublishSink(SrtSocket socket, TsMuxer muxer) { this.socket = socket; this.muxer = muxer; }

    public static Task<SrtPublishSink> ConnectAsync(SrtEndpoint endpoint, VideoFormat? video, AudioFormat? audio, int latencyMs, CancellationToken cancellation) =>
        Task.Run(() =>
        {
            var socket = SrtSocket.Create();
            // srt_connect/accept block; closing the socket is the only way to abandon them.
            using var registration = cancellation.Register(socket.Dispose);
            try
            {
                socket.Configure(latencyMs, endpoint.StreamId, endpoint.Passphrase, endpoint.KeyLength);
                if (!endpoint.Listener) socket.Connect(endpoint.Host, endpoint.Port);
                else
                {
                    socket.Listen(IPAddress.TryParse(endpoint.Host, out var address) ? address : IPAddress.Any, endpoint.Port, 1);
                    SrtSocket? caller = null;
                    while (caller is null) { cancellation.ThrowIfCancellationRequested(); caller = socket.Accept(500); }
                    socket.Dispose();
                    return new SrtPublishSink(caller, new TsMuxer(video, audio));
                }
                cancellation.ThrowIfCancellationRequested();
                return new SrtPublishSink(socket, new TsMuxer(video, audio));
            }
            catch { socket.Dispose(); cancellation.ThrowIfCancellationRequested(); throw; }
        }, cancellation);

    public long BytesSent => Interlocked.Read(ref bytesSent);
    public SrtStatistics? SrtStatistics { get { try { return socket.Statistics(clear: false); } catch (SrtException) { return null; } } }
    public void WriteVideo(VideoFrame frame) => Send(muxer.WriteVideo(frame));
    public void WriteAudio(AudioFrame frame) => Send(muxer.WriteAudio(frame));

    private void Send(byte[] packets)
    {
        for (var offset = 0; offset < packets.Length; offset += SrtSocket.LivePayloadSize)
            socket.Send(packets.AsSpan(offset, Math.Min(SrtSocket.LivePayloadSize, packets.Length - offset)));
        Interlocked.Add(ref bytesSent, packets.Length);
    }

    public void Dispose() => socket.Dispose();
}

public sealed class RtmpPublishSink : IMediaSink
{
    private const int VideoChannel = 6, AudioChannel = 4, DataChannel = 5;
    private readonly RtmpConnection connection;
    private readonly int streamId;
    private readonly VideoFormat? video;
    private readonly CancellationTokenSource reader = new();
    private long baseUs = long.MinValue;
    private volatile string? failure;

    private RtmpPublishSink(RtmpConnection connection, int streamId, VideoFormat? video)
    {
        this.connection = connection; this.streamId = streamId; this.video = video;
    }

    public static async Task<RtmpPublishSink> ConnectAsync(RtmpEndpoint endpoint, VideoFormat? video, AudioFormat? audio, int videoBitrateKbps,
        int audioBitrateKbps, CancellationToken cancellation)
    {
        var connection = await RtmpConnection.ConnectAsync(endpoint.Host, endpoint.Port, endpoint.Tls, cancellation);
        try
        {
            connection.SetChunkSize(4096);
            var properties = new Dictionary<string, object?>
            {
                ["app"] = endpoint.App, ["type"] = "nonprivate", ["flashVer"] = "FMLE/3.0 (compatible; DeveLiveStreamer)",
                ["tcUrl"] = endpoint.TcUrl
            };
            if (video?.Codec == VideoCodec.H265) properties["fourCcList"] = new List<object?> { "hvc1", "avc1" };
            connection.SendCommand(0, "connect", 1, properties);
            await connection.WaitForResultAsync(1, cancellation);
            connection.SendCommand(0, "releaseStream", 2, null, endpoint.StreamName);
            connection.SendCommand(0, "FCPublish", 3, null, endpoint.StreamName);
            connection.SendCommand(0, "createStream", 4, [null]);
            var created = await connection.WaitForResultAsync(4, cancellation);
            var streamId = created.Count > 3 && created[3] is double id ? (int)id : 1;
            connection.SendCommand(streamId, "publish", 5, null, endpoint.StreamName, "live");
            await WaitForPublishStartAsync(connection, cancellation);
            var sink = new RtmpPublishSink(connection, streamId, video);
            sink.SendMetadata(video, audio, videoBitrateKbps, audioBitrateKbps);
            if (video is not null) connection.Send(VideoChannel, RtmpMessage.Video, 0, streamId, FlvMedia.VideoSequenceHeader(video));
            if (audio is not null) connection.Send(AudioChannel, RtmpMessage.Audio, 0, streamId, FlvMedia.AudioSequenceHeader(audio));
            _ = sink.ReadAsync();
            return sink;
        }
        catch { connection.Dispose(); throw; }
    }

    private static async Task WaitForPublishStartAsync(RtmpConnection connection, CancellationToken cancellation)
    {
        while (true)
        {
            var message = await connection.ReadMessageAsync(cancellation) ?? throw new IOException("The RTMP server closed the connection before publishing started.");
            if (message.TypeId != RtmpMessage.CommandAmf0) continue;
            var values = Amf0.Decode(message.Payload);
            if (values.Count == 0) continue;
            if (values[0] as string == "_error") throw new IOException("The RTMP server rejected publishing: " + RtmpConnection.Describe(values));
            if (values[0] as string != "onStatus") continue;
            var status = values.OfType<IDictionary<string, object?>>().FirstOrDefault();
            var code = status?.TryGetValue("code", out var value) == true ? value as string : null;
            if (code == "NetStream.Publish.Start") return;
            if (code is not null && (code.Contains("Fail") || code.Contains("Bad") || code.Contains("Reject") || code.Contains("Error") ||
                status?.TryGetValue("level", out var level) == true && level as string == "error"))
                throw new IOException($"The RTMP server rejected publishing ({code}): {RtmpConnection.Describe(values)}");
        }
    }

    private void SendMetadata(VideoFormat? video, AudioFormat? audio, int videoBitrateKbps, int audioBitrateKbps)
    {
        var metadata = new AmfEcmaArray { ["duration"] = 0d, ["encoder"] = "DeveLiveStreamer" };
        if (video is not null)
        {
            metadata["width"] = (double)video.Width; metadata["height"] = (double)video.Height;
            metadata["framerate"] = video.FrameRate; metadata["videodatarate"] = (double)videoBitrateKbps;
            metadata["videocodecid"] = video.Codec == VideoCodec.H264 ? 7d : 0x68766331; // 'hvc1' for enhanced RTMP
        }
        if (audio is not null)
        {
            metadata["audiocodecid"] = 10d; metadata["audiodatarate"] = (double)audioBitrateKbps;
            metadata["audiosamplerate"] = (double)audio.SampleRate; metadata["audiosamplesize"] = 16d;
            metadata["stereo"] = audio.Channels > 1;
        }
        connection.Send(DataChannel, RtmpMessage.DataAmf0, 0, streamId, Amf0.Encode("@setDataFrame", "onMetaData", metadata));
    }

    private async Task ReadAsync()
    {
        try
        {
            while (!reader.IsCancellationRequested)
            {
                var message = await connection.ReadMessageAsync(reader.Token);
                if (message is null) { failure = "The RTMP server closed the connection."; return; }
                if (message.TypeId != RtmpMessage.CommandAmf0) continue;
                var values = Amf0.Decode(message.Payload);
                if (values.Count > 0 && values[0] as string == "onStatus" &&
                    values.OfType<IDictionary<string, object?>>().FirstOrDefault()?.TryGetValue("level", out var level) == true && level as string == "error")
                    failure = "The RTMP server stopped the stream: " + RtmpConnection.Describe(values);
            }
        }
        catch (Exception ex) when (!reader.IsCancellationRequested) { failure = ex.Message; }
        catch { }
    }

    public long BytesSent => connection.BytesSent;
    public SrtStatistics? SrtStatistics => null;

    public void WriteVideo(VideoFrame frame)
    {
        ThrowIfFailed();
        connection.Send(VideoChannel, RtmpMessage.Video, Timestamp(frame.DtsUs), streamId, FlvMedia.VideoFrame(video!.Codec, frame));
    }
    public void WriteAudio(AudioFrame frame)
    {
        ThrowIfFailed();
        connection.Send(AudioChannel, RtmpMessage.Audio, Timestamp(frame.PtsUs), streamId, FlvMedia.AudioFrame(frame));
    }

    private uint Timestamp(long us)
    {
        if (baseUs == long.MinValue) baseUs = us;
        return (uint)Math.Max(0, (us - baseUs) / 1000);
    }

    private void ThrowIfFailed() { if (failure is { } message) throw new IOException(message); }

    public void Dispose()
    {
        reader.Cancel();
        try { connection.SendCommand(streamId, "deleteStream", 6, null, (double)streamId); connection.Flush(); } catch { }
        connection.Dispose();
    }
}
