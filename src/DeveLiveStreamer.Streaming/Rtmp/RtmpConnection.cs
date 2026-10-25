using System.Buffers.Binary;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using DeveLiveStreamer.Streaming.Flv;

namespace DeveLiveStreamer.Streaming.Rtmp;

public sealed record RtmpMessage(int TypeId, uint Timestamp, int StreamId, byte[] Payload)
{
    public const int SetChunkSize = 1, Abort = 2, Acknowledgement = 3, UserControl = 4, WindowAcknowledgementSize = 5,
        SetPeerBandwidth = 6, Audio = 8, Video = 9, DataAmf0 = 18, CommandAmf0 = 20;
}

/// <summary>
/// RTMP chunk stream over TCP or TLS. Reads happen on one task; writes may come from any thread and are serialized.
/// Protocol control (chunk size, acknowledgements, ping) is handled here.
/// </summary>
public sealed class RtmpConnection : IAsyncDisposable, IDisposable
{
    private const int HandshakeSize = 1536;
    private readonly TcpClient client;
    private readonly Stream stream;
    private readonly object writeLock = new();
    private readonly Dictionary<int, ChunkState> incoming = [];
    private readonly byte[] header = new byte[18];
    private int inChunkSize = 128, outChunkSize = 128;
    private long bytesReceived, lastAcknowledged, windowSize;
    private bool disposed;

    private RtmpConnection(TcpClient client, Stream stream) { this.client = client; this.stream = stream; }
    public long BytesSent { get; private set; }

    public static async Task<RtmpConnection> ConnectAsync(string host, int port, bool tls, CancellationToken cancellation)
    {
        var client = new TcpClient { NoDelay = true };
        try
        {
            await client.ConnectAsync(host, port, cancellation);
            Stream stream = client.GetStream();
            if (tls)
            {
                var secure = new SslStream(stream, false);
                await secure.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host }, cancellation);
                stream = secure;
            }
            var connection = new RtmpConnection(client, stream);
            await connection.ClientHandshakeAsync(cancellation);
            return connection;
        }
        catch { client.Dispose(); throw; }
    }

    public static async Task<RtmpConnection> AcceptAsync(TcpClient client, CancellationToken cancellation)
    {
        client.NoDelay = true;
        var connection = new RtmpConnection(client, client.GetStream());
        try { await connection.ServerHandshakeAsync(cancellation); return connection; }
        catch { connection.Dispose(); throw; }
    }

    private async Task ClientHandshakeAsync(CancellationToken cancellation)
    {
        // Simple (non-digest) handshake: C0 version 3, C1 = time, zero, random.
        var c0c1 = new byte[1 + HandshakeSize];
        c0c1[0] = 3;
        RandomNumberGenerator.Fill(c0c1.AsSpan(9));
        await stream.WriteAsync(c0c1, cancellation);
        var s0s1 = new byte[1 + HandshakeSize];
        await stream.ReadExactlyAsync(s0s1, cancellation);
        if (s0s1[0] != 3) throw new IOException($"The RTMP server answered with unsupported version {s0s1[0]}.");
        var s2 = new byte[HandshakeSize];
        await stream.ReadExactlyAsync(s2, cancellation);
        await stream.WriteAsync(s0s1.AsMemory(1), cancellation);
    }

    private async Task ServerHandshakeAsync(CancellationToken cancellation)
    {
        var c0c1 = new byte[1 + HandshakeSize];
        await stream.ReadExactlyAsync(c0c1, cancellation);
        if (c0c1[0] != 3) throw new IOException($"Unsupported RTMP client version {c0c1[0]}.");
        var response = new byte[1 + 2 * HandshakeSize];
        response[0] = 3;
        // A zero version field tells clients that no digest validation is expected.
        RandomNumberGenerator.Fill(response.AsSpan(9, HandshakeSize - 8));
        c0c1.AsSpan(1).CopyTo(response.AsSpan(1 + HandshakeSize));
        await stream.WriteAsync(response, cancellation);
        var c2 = new byte[HandshakeSize];
        await stream.ReadExactlyAsync(c2, cancellation);
    }

    public void SetChunkSize(int size)
    {
        Span<byte> value = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(value, size);
        lock (writeLock) { Send(2, RtmpMessage.SetChunkSize, 0, 0, value); outChunkSize = size; }
    }

    public void SendControl(int type, uint value, byte? extra = null)
    {
        Span<byte> payload = stackalloc byte[5];
        BinaryPrimitives.WriteUInt32BigEndian(payload, value);
        if (extra is not null) payload[4] = extra.Value;
        Send(2, type, 0, 0, payload[..(extra is null ? 4 : 5)]);
    }

    public void SendUserControl(ushort eventType, uint value)
    {
        Span<byte> payload = stackalloc byte[6];
        BinaryPrimitives.WriteUInt16BigEndian(payload, eventType);
        BinaryPrimitives.WriteUInt32BigEndian(payload[2..], value);
        Send(2, RtmpMessage.UserControl, 0, 0, payload);
    }

    public void SendCommand(int streamId, string name, double transaction, params object?[] arguments) =>
        Send(streamId == 0 ? 3 : 8, RtmpMessage.CommandAmf0, 0, streamId, Amf0.Encode([name, transaction, .. arguments]));

    public void Send(int chunkStreamId, int typeId, uint timestamp, int streamId, ReadOnlySpan<byte> payload)
    {
        lock (writeLock) SendLocked(chunkStreamId, typeId, timestamp, streamId, payload);
    }

    private void SendLocked(int chunkStreamId, int typeId, uint timestamp, int streamId, ReadOnlySpan<byte> payload)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var extended = timestamp >= 0xFFFFFF;
        var chunks = Math.Max(1, (payload.Length + outChunkSize - 1) / outChunkSize);
        var buffer = new byte[payload.Length + 12 + (extended ? 4 : 0) + (chunks - 1) * (1 + (extended ? 4 : 0))];
        var position = 0;
        buffer[position++] = (byte)chunkStreamId; // fmt 0, chunk stream IDs used here are always < 64
        var ts = extended ? 0xFFFFFFu : timestamp;
        buffer[position++] = (byte)(ts >> 16); buffer[position++] = (byte)(ts >> 8); buffer[position++] = (byte)ts;
        buffer[position++] = (byte)(payload.Length >> 16); buffer[position++] = (byte)(payload.Length >> 8); buffer[position++] = (byte)payload.Length;
        buffer[position++] = (byte)typeId;
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(position), streamId); position += 4;
        if (extended) { BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(position), timestamp); position += 4; }
        for (var offset = 0; offset < payload.Length || offset == 0; offset += outChunkSize)
        {
            if (offset > 0)
            {
                buffer[position++] = (byte)(0xC0 | chunkStreamId);
                if (extended) { BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(position), timestamp); position += 4; }
            }
            var length = Math.Min(outChunkSize, payload.Length - offset);
            payload.Slice(offset, length).CopyTo(buffer.AsSpan(position));
            position += length;
            if (payload.Length == 0) break;
        }
        stream.Write(buffer, 0, position);
        BytesSent += position;
    }

    public void Flush() { lock (writeLock) stream.Flush(); }

    /// <summary>Reads the next complete message. Returns null when the peer closes the connection.</summary>
    public async Task<RtmpMessage?> ReadMessageAsync(CancellationToken cancellation)
    {
        while (true)
        {
            if (!await ReadExactlyOrEndAsync(header.AsMemory(0, 1), cancellation)) return null;
            var format = header[0] >> 6;
            var chunkStreamId = header[0] & 0x3F;
            if (chunkStreamId == 0) { await Read(1); chunkStreamId = 64 + header[0]; }
            else if (chunkStreamId == 1) { await Read(2); chunkStreamId = 64 + header[0] + header[1] * 256; }
            if (!incoming.TryGetValue(chunkStreamId, out var state)) incoming[chunkStreamId] = state = new ChunkState();
            var headerLength = format switch { 0 => 11, 1 => 7, 2 => 3, _ => 0 };
            if (headerLength > 0) await Read(headerLength);
            uint field = 0;
            if (format < 3)
            {
                field = (uint)((header[0] << 16) | (header[1] << 8) | header[2]);
                if (format < 2) { state.Length = (header[3] << 16) | (header[4] << 8) | header[5]; state.TypeId = header[6]; }
                if (format == 0) state.StreamId = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(7));
                state.Extended = field == 0xFFFFFF;
            }
            if (state.Extended) { await Read(4); field = BinaryPrimitives.ReadUInt32BigEndian(header); }
            if (state.Received == 0)
            {
                // Like FFmpeg, a type-3 header after type 0 reuses the previous timestamp field as its delta.
                if (format == 0) { state.Timestamp = field; state.Delta = field; }
                else if (format < 3) { state.Delta = field; state.Timestamp += field; }
                else state.Timestamp += state.Delta;
                if (state.Payload is null || state.Payload.Length != state.Length) state.Payload = new byte[state.Length];
            }
            var size = Math.Min(inChunkSize, state.Length - state.Received);
            if (size > 0) await stream.ReadExactlyAsync(state.Payload!.AsMemory(state.Received, size), cancellation);
            state.Received += size;
            Count(size + 1 + headerLength);
            if (state.Received < state.Length) continue;
            state.Received = 0;
            var message = new RtmpMessage(state.TypeId, state.Timestamp, state.StreamId, state.Payload!);
            state.Payload = null;
            if (Handle(message)) return message;
        }

        async Task Read(int count)
        {
            if (!await ReadExactlyOrEndAsync(header.AsMemory(0, count), cancellation)) throw new EndOfStreamException();
        }
    }

    private bool Handle(RtmpMessage message)
    {
        var payload = message.Payload;
        switch (message.TypeId)
        {
            case RtmpMessage.SetChunkSize when payload.Length >= 4:
                inChunkSize = Math.Max(1, BinaryPrimitives.ReadInt32BigEndian(payload) & 0x7FFFFFFF); return false;
            case RtmpMessage.WindowAcknowledgementSize when payload.Length >= 4:
                windowSize = BinaryPrimitives.ReadUInt32BigEndian(payload); return false;
            case RtmpMessage.Abort or RtmpMessage.Acknowledgement or RtmpMessage.SetPeerBandwidth: return false;
            case RtmpMessage.UserControl when payload.Length >= 6 && BinaryPrimitives.ReadUInt16BigEndian(payload) == 6:
                // PingRequest → PingResponse
                SendUserControl(7, BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(2))); return false;
            default: return true;
        }
    }

    private void Count(int bytes)
    {
        bytesReceived += bytes;
        if (windowSize > 0 && bytesReceived - lastAcknowledged >= windowSize)
        {
            lastAcknowledged = bytesReceived;
            try { SendControl(RtmpMessage.Acknowledgement, (uint)bytesReceived); } catch (ObjectDisposedException) { }
        }
    }

    private async Task<bool> ReadExactlyOrEndAsync(Memory<byte> buffer, CancellationToken cancellation)
    {
        var read = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellation);
        if (read == 0) return false;
        if (read < buffer.Length) throw new EndOfStreamException();
        return true;
    }

    /// <summary>Waits for _result/_error of a command transaction, ignoring unrelated messages.</summary>
    public async Task<List<object?>> WaitForResultAsync(double transaction, CancellationToken cancellation)
    {
        while (true)
        {
            var message = await ReadMessageAsync(cancellation) ?? throw new IOException("The RTMP server closed the connection.");
            if (message.TypeId != RtmpMessage.CommandAmf0) continue;
            var values = Amf0.Decode(message.Payload);
            if (values.Count < 2 || values[1] is not double id || id != transaction) continue;
            if (values[0] as string == "_error") throw new IOException("The RTMP server rejected the request: " + Describe(values));
            if (values[0] as string == "_result") return values;
        }
    }

    internal static string Describe(List<object?> values) =>
        values.OfType<IDictionary<string, object?>>().Select(d => (d.TryGetValue("description", out var text) ? text as string : null)
            ?? (d.TryGetValue("code", out var code) ? code as string : null)).FirstOrDefault(s => !string.IsNullOrEmpty(s)) ?? "no details";

    public void Dispose()
    {
        lock (writeLock)
        {
            if (disposed) return;
            disposed = true;
        }
        stream.Dispose(); client.Dispose();
    }
    public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }

    private sealed class ChunkState
    {
        public int Length, TypeId, StreamId, Received;
        public uint Timestamp, Delta;
        public bool Extended;
        public byte[]? Payload;
    }
}
