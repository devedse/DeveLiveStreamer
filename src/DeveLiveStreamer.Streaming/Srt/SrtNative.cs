using System.Reflection;
using System.Runtime.InteropServices;

namespace DeveLiveStreamer.Streaming.Srt;

internal enum SrtOption
{
    SndSyn = 1, RcvSyn = 2, SndBuf = 5, RcvBuf = 6, Linger = 7, RcvTimeout = 14, ReuseAddr = 15, MaxBw = 16,
    Sender = 21, TsbPdMode = 22, Latency = 23, Passphrase = 26, PbKeyLen = 27, ConnTimeout = 36,
    RcvLatency = 43, PeerLatency = 44, StreamId = 46, PayloadSize = 49, TransType = 50, PeerIdleTimeout = 55
}

public enum SrtSocketStatus { Init = 1, Opened, Listening, Connecting, Connected, Broken, Closing, Closed, NonExistent }

/// <summary>libsrt 1.5 CBytePerfMon. Field order and types must match srt.h exactly.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct SrtPerfMon
{
    public long msTimeStamp, pktSentTotal, pktRecvTotal;
    public int pktSndLossTotal, pktRcvLossTotal, pktRetransTotal, pktSentACKTotal, pktRecvACKTotal, pktSentNAKTotal, pktRecvNAKTotal;
    public long usSndDurationTotal;
    public int pktSndDropTotal, pktRcvDropTotal, pktRcvUndecryptTotal;
    public ulong byteSentTotal, byteRecvTotal, byteRcvLossTotal, byteRetransTotal, byteSndDropTotal, byteRcvDropTotal, byteRcvUndecryptTotal;
    public long pktSent, pktRecv;
    public int pktSndLoss, pktRcvLoss, pktRetrans, pktRcvRetrans, pktSentACK, pktRecvACK, pktSentNAK, pktRecvNAK;
    public double mbpsSendRate, mbpsRecvRate;
    public long usSndDuration;
    public int pktReorderDistance;
    public double pktRcvAvgBelatedTime;
    public long pktRcvBelated;
    public int pktSndDrop, pktRcvDrop, pktRcvUndecrypt;
    public ulong byteSent, byteRecv, byteRcvLoss, byteRetrans, byteSndDrop, byteRcvDrop, byteRcvUndecrypt;
    public double usPktSndPeriod;
    public int pktFlowWindow, pktCongestionWindow, pktFlightSize;
    public double msRTT, mbpsBandwidth;
    public int byteAvailSndBuf, byteAvailRcvBuf;
    public double mbpsMaxBW;
    public int byteMSS, pktSndBuf, byteSndBuf, msSndBuf, msSndTsbPdDelay, pktRcvBuf, byteRcvBuf, msRcvBuf, msRcvTsbPdDelay;
    public int pktSndFilterExtraTotal, pktRcvFilterExtraTotal, pktRcvFilterSupplyTotal, pktRcvFilterLossTotal;
    public int pktSndFilterExtra, pktRcvFilterExtra, pktRcvFilterSupply, pktRcvFilterLoss, pktReorderTolerance;
    public long pktSentUniqueTotal, pktRecvUniqueTotal;
    public ulong byteSentUniqueTotal, byteRecvUniqueTotal;
    public long pktSentUnique, pktRecvUnique;
    public ulong byteSentUnique, byteRecvUnique;
}

internal static unsafe class SrtNative
{
    private const string Library = "srt";
    public const int Error = -1;
    public const int AsyncReceive = 6002, Timeout = 6003;

    static SrtNative()
    {
        NativeLibrary.SetDllImportResolver(typeof(SrtNative).Assembly, Resolve);
        if (srt_startup() < 0) throw new SrtException("SRT could not start: " + LastError);
    }

    /// <summary>Forces static initialization (library resolution and srt_startup).</summary>
    public static void EnsureStarted() { }

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? path)
    {
        if (name != Library) return IntPtr.Zero;
        // Android ships libsrt inside srtdroid's JNI library; desktop tests use the system libsrt 1.5.
        foreach (var candidate in new[] { "libsrtdroid.so", "libsrt.so.1.5", "libsrt-gnutls.so.1.5", "libsrt-openssl.so.1.5", "libsrt.so", "libsrt.dylib", "srt" })
            if (NativeLibrary.TryLoad(candidate, assembly, path, out var handle)) return handle;
        return IntPtr.Zero;
    }

    public static string LastError => Marshal.PtrToStringUTF8(srt_getlasterror_str()) ?? "unknown SRT error";
    public static int LastErrorCode => srt_getlasterror(null);

    [DllImport(Library)] public static extern int srt_startup();
    [DllImport(Library)] public static extern int srt_create_socket();
    [DllImport(Library)] public static extern int srt_close(int socket);
    [DllImport(Library)] public static extern int srt_setsockflag(int socket, SrtOption option, void* value, int length);
    [DllImport(Library)] public static extern int srt_getsockflag(int socket, SrtOption option, void* value, int* length);
    [DllImport(Library)] public static extern int srt_connect(int socket, byte* address, int length);
    [DllImport(Library)] public static extern int srt_bind(int socket, byte* address, int length);
    [DllImport(Library)] public static extern int srt_listen(int socket, int backlog);
    [DllImport(Library)] public static extern int srt_accept(int socket, byte* address, int* length);
    [DllImport(Library)] public static extern int srt_send(int socket, byte* buffer, int length);
    [DllImport(Library)] public static extern int srt_recv(int socket, byte* buffer, int length);
    [DllImport(Library)] public static extern int srt_bstats(int socket, SrtPerfMon* stats, int clear);
    [DllImport(Library)] public static extern SrtSocketStatus srt_getsockstate(int socket);
    [DllImport(Library)] public static extern IntPtr srt_getlasterror_str();
    [DllImport(Library)] public static extern int srt_getlasterror(int* systemError);
    [DllImport(Library)] public static extern int srt_epoll_create();
    [DllImport(Library)] public static extern int srt_epoll_add_usock(int epoll, int socket, int* events);
    [DllImport(Library)] public static extern int srt_epoll_wait(int epoll, int* readSockets, int* readCount, int* writeSockets, int* writeCount,
        long timeoutMs, int* systemRead, int* systemReadCount, int* systemWrite, int* systemWriteCount);
    [DllImport(Library)] public static extern int srt_epoll_release(int epoll);
}

public sealed class SrtException(string message) : IOException(message);
