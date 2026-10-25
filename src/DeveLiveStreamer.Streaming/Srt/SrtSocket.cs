using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace DeveLiveStreamer.Streaming.Srt;

public sealed record SrtStatistics(double RttMs, double BandwidthMbps, double SendRateMbps, long PacketsLost, long PacketsRetransmitted,
    long PacketsDropped, int SendBufferMs, int NegotiatedLatencyMs, long BytesSent, long BytesReceived);

/// <summary>A live-mode SRT socket (caller or listener) carrying 1316-byte MPEG-TS messages.</summary>
public sealed unsafe class SrtSocket : IDisposable
{
    public const int LivePayloadSize = 1316;
    private int handle;

    private SrtSocket(int handle) => this.handle = handle;

    public static SrtSocket Create()
    {
        try { SrtNative.EnsureStarted(); }
        catch (TypeInitializationException ex) { throw new SrtException("The SRT library could not be loaded: " + (ex.InnerException?.Message ?? ex.Message)); }
        var handle = SrtNative.srt_create_socket();
        if (handle == SrtNative.Error) throw new SrtException("Could not create an SRT socket: " + SrtNative.LastError);
        var socket = new SrtSocket(handle);
        socket.Set(SrtOption.TransType, 0); // SRTT_LIVE
        return socket;
    }

    public SrtSocketStatus Status => handle == 0 ? SrtSocketStatus.Closed : SrtNative.srt_getsockstate(handle);

    internal void Set(SrtOption option, int value) => Check(SrtNative.srt_setsockflag(handle, option, &value, sizeof(int)), $"set option {option}");
    internal void Set(SrtOption option, bool value) { var flag = value ? 1 : 0; Check(SrtNative.srt_setsockflag(handle, option, &flag, sizeof(int)), $"set option {option}"); }
    internal void Set(SrtOption option, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        fixed (byte* pointer = bytes) Check(SrtNative.srt_setsockflag(handle, option, pointer, bytes.Length), $"set option {option}");
    }
    internal int GetInt(SrtOption option)
    {
        int value = 0, length = sizeof(int);
        Check(SrtNative.srt_getsockflag(handle, option, &value, &length), $"read option {option}");
        return value;
    }

    public string StreamId
    {
        get
        {
            var buffer = stackalloc byte[513];
            var length = 512;
            if (SrtNative.srt_getsockflag(handle, SrtOption.StreamId, buffer, &length) == SrtNative.Error) return "";
            return Encoding.UTF8.GetString(buffer, length);
        }
    }

    /// <summary>Applies latency (ms, zero allowed), encryption and the stream ID before connecting.</summary>
    public void Configure(int latencyMs, string? streamId, string? passphrase, int keyLength)
    {
        Set(SrtOption.Latency, latencyMs);
        if (!string.IsNullOrEmpty(streamId)) Set(SrtOption.StreamId, streamId);
        if (!string.IsNullOrEmpty(passphrase))
        {
            Set(SrtOption.Passphrase, passphrase);
            if (keyLength > 0) Set(SrtOption.PbKeyLen, keyLength);
        }
    }

    public void Connect(string host, int port, int timeoutMs = 5000)
    {
        Set(SrtOption.ConnTimeout, timeoutMs);
        var addresses = IPAddress.TryParse(host, out var literal) ? [literal]
            : Dns.GetHostAddresses(host).OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1).ToArray();
        if (addresses.Length == 0) throw new SrtException($"Could not resolve {host}.");
        var address = Address(new IPEndPoint(addresses[0], port));
        fixed (byte* pointer = address)
            Check(SrtNative.srt_connect(handle, pointer, address.Length), $"connect to {host}:{port}");
    }

    public void Listen(IPAddress address, int port, int backlog = 4)
    {
        Set(SrtOption.ReuseAddr, true);
        var bytes = Address(new IPEndPoint(address, port));
        fixed (byte* pointer = bytes) Check(SrtNative.srt_bind(handle, pointer, bytes.Length), $"listen on port {port}");
        Check(SrtNative.srt_listen(handle, backlog), $"listen on port {port}");
    }

    /// <summary>Waits up to <paramref name="timeoutMs"/> for a caller; returns null on timeout.</summary>
    public SrtSocket? Accept(int timeoutMs)
    {
        var epoll = SrtNative.srt_epoll_create();
        try
        {
            var events = 0x1 | 0x8; // SRT_EPOLL_IN | SRT_EPOLL_ERR
            SrtNative.srt_epoll_add_usock(epoll, handle, &events);
            int ready = 0, readyCount = 1;
            if (SrtNative.srt_epoll_wait(epoll, &ready, &readyCount, null, null, timeoutMs, null, null, null, null) <= 0) return null;
        }
        finally { SrtNative.srt_epoll_release(epoll); }
        var address = stackalloc byte[128];
        var length = 128;
        var accepted = SrtNative.srt_accept(handle, address, &length);
        if (accepted == SrtNative.Error) throw new SrtException("SRT accept failed: " + SrtNative.LastError);
        return new SrtSocket(accepted);
    }

    public void Send(ReadOnlySpan<byte> message)
    {
        fixed (byte* pointer = message)
            if (SrtNative.srt_send(handle, pointer, message.Length) == SrtNative.Error) throw new SrtException("SRT send failed: " + SrtNative.LastError);
    }

    /// <summary>Receives one message. Returns 0 when the receive timeout elapsed; throws when the connection is gone.</summary>
    public int Receive(Span<byte> buffer)
    {
        fixed (byte* pointer = buffer)
        {
            var received = SrtNative.srt_recv(handle, pointer, buffer.Length);
            if (received != SrtNative.Error) return received;
            var code = SrtNative.LastErrorCode;
            if (code is SrtNative.AsyncReceive or SrtNative.Timeout) return 0;
            throw new SrtException("SRT receive failed: " + SrtNative.LastError);
        }
    }

    public void SetReceiveTimeout(int milliseconds) => Set(SrtOption.RcvTimeout, milliseconds);

    public SrtStatistics Statistics(bool clear = true)
    {
        SrtPerfMon stats;
        if (SrtNative.srt_bstats(handle, &stats, clear ? 1 : 0) == SrtNative.Error) throw new SrtException(SrtNative.LastError);
        return new SrtStatistics(stats.msRTT, stats.mbpsBandwidth, stats.mbpsSendRate, stats.pktSndLossTotal, stats.pktRetransTotal,
            stats.pktSndDropTotal, stats.msSndBuf, stats.msSndTsbPdDelay, (long)stats.byteSentTotal, (long)stats.byteRecvTotal);
    }

    public void Dispose()
    {
        var current = Interlocked.Exchange(ref handle, 0);
        if (current != 0) SrtNative.srt_close(current);
    }

    private static void Check(int result, string action)
    {
        if (result == SrtNative.Error) throw new SrtException($"Could not {action}: {SrtNative.LastError}");
    }

    /// <summary>Builds a native sockaddr_in/sockaddr_in6 (Linux/Android layout, or BSD layout on Apple platforms).</summary>
    internal static byte[] Address(IPEndPoint endpoint)
    {
        var v6 = endpoint.AddressFamily == AddressFamily.InterNetworkV6;
        var bytes = new byte[v6 ? 28 : 16];
        var apple = OperatingSystem.IsIOS() || OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst() || OperatingSystem.IsTvOS();
        if (apple) { bytes[0] = (byte)bytes.Length; bytes[1] = (byte)(v6 ? 30 : 2); }
        else BinaryPrimitives.WriteUInt16LittleEndian(bytes, (ushort)(v6 ? 10 : 2));
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(2), (ushort)endpoint.Port);
        if (v6)
        {
            endpoint.Address.GetAddressBytes().CopyTo(bytes, 8);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24), (uint)endpoint.Address.ScopeId);
        }
        else endpoint.Address.GetAddressBytes().CopyTo(bytes, 4);
        return bytes;
    }
}
