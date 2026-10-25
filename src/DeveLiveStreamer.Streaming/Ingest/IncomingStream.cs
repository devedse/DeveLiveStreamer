using System.Net;
using System.Net.Sockets;
using DeveLiveStreamer.Streaming.Flv;
using DeveLiveStreamer.Streaming.Media;
using DeveLiveStreamer.Streaming.MpegTs;
using DeveLiveStreamer.Streaming.Rtmp;
using DeveLiveStreamer.Streaming.Srt;

namespace DeveLiveStreamer.Streaming.Ingest;

public enum IncomingState { Starting, Waiting, Connecting, Receiving, Retrying, Stopped }

/// <summary>
/// An incoming RTMP or SRT stream used as a scene source: either pulled from a URL, or received by
/// listening on this device (rtmp://0.0.0.0:1935/app/key, srt://0.0.0.0:9000).
/// </summary>
public abstract class IncomingStream : IMediaReceiver, IAsyncDisposable
{
    private readonly CancellationTokenSource stopping = new();
    private Task? worker;

    public event Action<VideoFormat>? VideoFormatChanged;
    public event Action<VideoFrame>? VideoFrameReceived;
    public event Action<AudioFormat>? AudioFormatChanged;
    public event Action<AudioFrame>? AudioFrameReceived;
    public event Action<IncomingState, string>? StateChanged;

    public IncomingState State { get; private set; } = IncomingState.Starting;
    public string StatusText { get; private set; } = "Starting";
    public abstract string Description { get; }

    public static IncomingStream Create(string url)
    {
        url = url.Trim();
        if (url.StartsWith("srt://", StringComparison.OrdinalIgnoreCase))
        {
            var endpoint = (SrtEndpoint)StreamEndpoint.Parse(url);
            return endpoint.Listener ? new SrtListenStream(endpoint) : new SrtPullStream(endpoint);
        }
        if (url.StartsWith("rtmp://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("rtmps://", StringComparison.OrdinalIgnoreCase))
        {
            var (host, port, rest) = StreamEndpoint.SplitAuthority(url, url.StartsWith("rtmps", StringComparison.OrdinalIgnoreCase) ? 443 : 1935);
            if (host is "0.0.0.0" or "::" or "*") return new RtmpListenStream(port, rest.Trim('/'));
            return new RtmpPullStream((RtmpEndpoint)StreamEndpoint.Parse(url));
        }
        throw new FormatException("Use an rtmp:// or srt:// URL. Listen for pushes with rtmp://0.0.0.0:1935/live/key or srt://0.0.0.0:9000.");
    }

    public void Start() => worker ??= Task.Run(async () =>
    {
        while (!stopping.IsCancellationRequested)
        {
            try { await RunAsync(stopping.Token); }
            catch (Exception ex) when (!stopping.IsCancellationRequested) { SetState(IncomingState.Retrying, ex.Message); }
            catch { break; }
            try { await Task.Delay(1500, stopping.Token); } catch (OperationCanceledException) { break; }
        }
        SetState(IncomingState.Stopped, "Stopped");
    });

    protected abstract Task RunAsync(CancellationToken cancellation);

    protected internal void SetState(IncomingState state, string text)
    {
        State = state; StatusText = text;
        try { StateChanged?.Invoke(state, text); } catch { }
    }

    protected internal void Attach(IMediaReceiver source)
    {
        source.VideoFormatChanged += f => VideoFormatChanged?.Invoke(f);
        source.VideoFrameReceived += f => VideoFrameReceived?.Invoke(f);
        source.AudioFormatChanged += f => AudioFormatChanged?.Invoke(f);
        source.AudioFrameReceived += f => AudioFrameReceived?.Invoke(f);
    }

    public async ValueTask DisposeAsync()
    {
        stopping.Cancel();
        OnStopping();
        if (worker is not null) try { await worker; } catch { }
    }

    protected virtual void OnStopping() { }
}

internal sealed class RtmpPullStream(RtmpEndpoint endpoint) : IncomingStream
{
    public override string Description => $"RTMP from {endpoint.Host}";

    protected override async Task RunAsync(CancellationToken cancellation)
    {
        SetState(IncomingState.Connecting, $"Connecting to {endpoint.Host}");
        await using var connection = await RtmpConnection.ConnectAsync(endpoint.Host, endpoint.Port, endpoint.Tls, cancellation);
        connection.SetChunkSize(4096);
        connection.SendCommand(0, "connect", 1, new Dictionary<string, object?>
        {
            ["app"] = endpoint.App, ["flashVer"] = "LNX 9,0,124,2", ["tcUrl"] = endpoint.TcUrl, ["fpad"] = false,
            ["capabilities"] = 15d, ["audioCodecs"] = 3191d, ["videoCodecs"] = 252d, ["videoFunction"] = 1d,
            ["fourCcList"] = new List<object?> { "avc1", "hvc1" }
        });
        await connection.WaitForResultAsync(1, cancellation);
        connection.SendControl(RtmpMessage.WindowAcknowledgementSize, 2_500_000);
        connection.SendCommand(0, "createStream", 2, [null]);
        var created = await connection.WaitForResultAsync(2, cancellation);
        var streamId = created.Count > 3 && created[3] is double id ? (int)id : 1;
        connection.SendCommand(streamId, "play", 0, null, endpoint.StreamName, -2000d);
        var parser = new FlvMediaParser();
        Attach(parser);
        var receiving = false;
        while (true)
        {
            var message = await connection.ReadMessageAsync(cancellation) ?? throw new IOException("The RTMP server ended the stream.");
            switch (message.TypeId)
            {
                case RtmpMessage.Video: parser.Video(message.Payload, message.Timestamp); break;
                case RtmpMessage.Audio: parser.Audio(message.Payload, message.Timestamp); break;
                case RtmpMessage.CommandAmf0:
                    var values = Amf0.Decode(message.Payload);
                    if (values.Count > 0 && values[0] as string == "onStatus" &&
                        values.OfType<IDictionary<string, object?>>().FirstOrDefault()?.TryGetValue("level", out var level) == true && level as string == "error")
                        throw new IOException(RtmpConnection.Describe(values));
                    continue;
                default: continue;
            }
            if (!receiving) { receiving = true; SetState(IncomingState.Receiving, $"Receiving from {endpoint.Host}"); }
        }
    }
}

internal sealed class RtmpListenStream : IncomingStream
{
    private readonly int port;
    private readonly string expectedPath;
    // One parser for the source: a reconnecting publisher re-sends its sequence headers.
    private readonly FlvMediaParser parser = new();
    private TcpListener? listener;
    private RtmpConnection? active;

    public RtmpListenStream(int port, string expectedPath)
    {
        this.port = port; this.expectedPath = expectedPath;
        Attach(parser);
    }

    public override string Description => $"RTMP server on port {port}";

    protected override async Task RunAsync(CancellationToken cancellation)
    {
        listener = new TcpListener(IPAddress.IPv6Any, port);
        listener.Server.DualMode = true;
        listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        listener.Start();
        try
        {
            SetState(IncomingState.Waiting, $"Waiting for a publisher on port {port}");
            while (true)
            {
                var client = await listener.AcceptTcpClientAsync(cancellation);
                _ = HandleAsync(client, cancellation);
            }
        }
        finally { listener.Stop(); }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken cancellation)
    {
        RtmpConnection? connection = null;
        string? error = null;
        try
        {
            connection = await RtmpConnection.AcceptAsync(client, cancellation);
            string app = "";
            const int streamId = 1;
            while (true)
            {
                var message = await connection.ReadMessageAsync(cancellation);
                if (message is null) break;
                if (message.TypeId is RtmpMessage.Video or RtmpMessage.Audio)
                {
                    if (active != connection) continue;
                    if (message.TypeId == RtmpMessage.Video) parser.Video(message.Payload, message.Timestamp);
                    else parser.Audio(message.Payload, message.Timestamp);
                    continue;
                }
                if (message.TypeId != RtmpMessage.CommandAmf0) continue;
                var values = Amf0.Decode(message.Payload);
                if (values.Count < 2 || values[0] is not string command) continue;
                var transaction = values[1] as double? ?? 0;
                switch (command)
                {
                    case "connect":
                        app = (values.ElementAtOrDefault(2) as IDictionary<string, object?>)?.TryGetValue("app", out var name) == true ? name as string ?? "" : "";
                        connection.SendControl(RtmpMessage.WindowAcknowledgementSize, 2_500_000);
                        connection.SendControl(RtmpMessage.SetPeerBandwidth, 2_500_000, 2);
                        connection.SetChunkSize(4096);
                        connection.SendCommand(0, "_result", transaction,
                            new Dictionary<string, object?> { ["fmsVer"] = "FMS/3,0,1,123", ["capabilities"] = 31d },
                            new Dictionary<string, object?> { ["level"] = "status", ["code"] = "NetConnection.Connect.Success", ["description"] = "Connection succeeded.", ["objectEncoding"] = 0d });
                        break;
                    case "createStream":
                        connection.SendCommand(0, "_result", transaction, null, (double)streamId);
                        break;
                    case "publish":
                        var stream = values.ElementAtOrDefault(3) as string ?? "";
                        if (expectedPath.Length > 0 && !Matches(app, stream))
                        {
                            connection.SendCommand(streamId, "onStatus", 0, null, Status("error", "NetStream.Publish.BadName", "Unknown application or stream key."));
                            await Task.Delay(200, cancellation);
                            return;
                        }
                        // The newest publisher replaces an older one, e.g. after the encoder reconnects.
                        Interlocked.Exchange(ref active, connection)?.Dispose();
                        connection.SendUserControl(0, streamId); // StreamBegin
                        connection.SendCommand(streamId, "onStatus", 0, null, Status("status", "NetStream.Publish.Start", $"Publishing {stream}."));
                        SetState(IncomingState.Receiving, $"Receiving {app}/{stream} from {(client.Client.RemoteEndPoint as IPEndPoint)?.Address}");
                        break;
                    case "deleteStream" or "FCUnpublish" or "closeStream":
                        if (active == connection) return;
                        break;
                    default:
                        if (transaction > 0) connection.SendCommand(0, "_result", transaction, null, AmfUndefined.Value);
                        break;
                }
            }
        }
        catch (Exception ex) when (!cancellation.IsCancellationRequested) { error = ex.Message; }
        catch (OperationCanceledException) { }
        finally
        {
            if (connection is not null && Interlocked.CompareExchange(ref active, null, connection) == connection)
                SetState(IncomingState.Waiting, $"Publisher disconnected{(error is null ? "" : $" ({error})")} · waiting on port {port}");
            connection?.Dispose();
            client.Dispose();
        }
    }

    private bool Matches(string app, string stream)
    {
        var expected = expectedPath.Split('?')[0];
        var actual = $"{app.Trim('/')}/{stream.Split('?')[0]}";
        return expected.Contains('/') ? actual.Equals(expected, StringComparison.Ordinal) : app.Trim('/') == expected;
    }

    private static Dictionary<string, object?> Status(string level, string code, string description) =>
        new() { ["level"] = level, ["code"] = code, ["description"] = description };

    protected override void OnStopping()
    {
        listener?.Stop();
        active?.Dispose();
    }
}

internal sealed class SrtPullStream(SrtEndpoint endpoint) : IncomingStream
{
    private SrtSocket? socket;
    public override string Description => $"SRT from {endpoint.Host}";

    protected override Task RunAsync(CancellationToken cancellation) => Task.Run(() =>
    {
        SetState(IncomingState.Connecting, $"Connecting to {endpoint.Host}:{endpoint.Port}");
        using var connected = socket = SrtSocket.Create();
        connected.Configure(120, endpoint.StreamId, endpoint.Passphrase, endpoint.KeyLength);
        connected.Connect(endpoint.Host, endpoint.Port);
        connected.SetReceiveTimeout(500);
        SrtReceive.Run(connected, this, $"Receiving from {endpoint.Host}", cancellation);
    }, cancellation);

    protected override void OnStopping() => socket?.Dispose();
}

internal sealed class SrtListenStream(SrtEndpoint endpoint) : IncomingStream
{
    private SrtSocket? listener, caller;
    public override string Description => $"SRT listener on port {endpoint.Port}";

    protected override Task RunAsync(CancellationToken cancellation) => Task.Run(() =>
    {
        using var listening = listener = SrtSocket.Create();
        listening.Configure(120, null, endpoint.Passphrase, endpoint.KeyLength);
        listening.Listen(IPAddress.Any, endpoint.Port);
        while (!cancellation.IsCancellationRequested)
        {
            if (State != IncomingState.Waiting) SetState(IncomingState.Waiting, $"Waiting for an SRT caller on port {endpoint.Port}");
            using var accepted = caller = listening.Accept(500);
            if (accepted is null) continue;
            accepted.SetReceiveTimeout(500);
            var streamId = accepted.StreamId;
            try { SrtReceive.Run(accepted, this, "Receiving" + (streamId.Length > 0 ? $" {streamId}" : ""), cancellation); }
            catch (SrtException) when (!cancellation.IsCancellationRequested) { }
        }
    }, cancellation);

    protected override void OnStopping() { caller?.Dispose(); listener?.Dispose(); }
}

internal static class SrtReceive
{
    public static void Run(SrtSocket socket, IncomingStream owner, string text, CancellationToken cancellation)
    {
        var demuxer = new TsDemuxer();
        owner.Attach(demuxer);
        var buffer = new byte[1500];
        var receiving = false;
        var idle = 0;
        while (!cancellation.IsCancellationRequested)
        {
            var length = socket.Receive(buffer);
            if (length == 0)
            {
                if (socket.Status is SrtSocketStatus.Broken or SrtSocketStatus.Closed or SrtSocketStatus.NonExistent || ++idle > 20)
                    throw new SrtException("The SRT stream stopped sending.");
                continue;
            }
            idle = 0;
            if (!receiving) { receiving = true; owner.SetState(IncomingState.Receiving, text); }
            demuxer.Push(buffer.AsSpan(0, length));
        }
    }
}
