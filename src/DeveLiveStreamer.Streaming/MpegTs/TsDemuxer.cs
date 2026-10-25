using DeveLiveStreamer.Streaming.Media;

namespace DeveLiveStreamer.Streaming.MpegTs;

/// <summary>Extracts the first H.264/HEVC and AAC (ADTS) elementary streams from an MPEG-TS byte stream.</summary>
public sealed class TsDemuxer : IMediaReceiver
{
    private readonly byte[] partial = new byte[TsMuxer.PacketSize];
    private readonly Dictionary<int, MemoryStream> pes = [];
    private int partialLength;
    private int pmtPid = -1, videoPid = -1, audioPid = -1;
    private VideoCodec videoCodec;
    private VideoFormat? videoFormat;
    private AudioFormat? audioFormat;
    private readonly TimestampUnwrapper videoClock = new(), audioClock = new();

    public event Action<VideoFormat>? VideoFormatChanged;
    public event Action<VideoFrame>? VideoFrameReceived;
    public event Action<AudioFormat>? AudioFormatChanged;
    public event Action<AudioFrame>? AudioFrameReceived;

    public void Push(ReadOnlySpan<byte> data)
    {
        if (partialLength > 0)
        {
            var needed = Math.Min(TsMuxer.PacketSize - partialLength, data.Length);
            data[..needed].CopyTo(partial.AsSpan(partialLength));
            partialLength += needed; data = data[needed..];
            if (partialLength < TsMuxer.PacketSize) return;
            Packet(partial); partialLength = 0;
        }
        while (data.Length > 0)
        {
            if (data[0] != 0x47)
            {
                var sync = data.IndexOf((byte)0x47);
                if (sync < 0) return;
                data = data[sync..];
            }
            if (data.Length < TsMuxer.PacketSize) { data.CopyTo(partial); partialLength = data.Length; return; }
            Packet(data[..TsMuxer.PacketSize]);
            data = data[TsMuxer.PacketSize..];
        }
    }

    /// <summary>Emits PES data held for streams whose packets have no explicit length.</summary>
    public void Flush() { foreach (var pid in pes.Keys.ToArray()) CompletePes(pid); }

    private void Packet(ReadOnlySpan<byte> packet)
    {
        var pid = ((packet[1] & 0x1F) << 8) | packet[2];
        var start = (packet[1] & 0x40) != 0;
        var control = (packet[3] >> 4) & 3;
        if ((control & 1) == 0) return;
        var offset = 4;
        if ((control & 2) != 0) offset += 1 + packet[4];
        if (offset >= TsMuxer.PacketSize) return;
        var payload = packet[offset..];
        if (pid == 0 && start) ParsePat(payload);
        else if (pid == pmtPid && start) ParsePmt(payload);
        else if (pid == videoPid || pid == audioPid)
        {
            if (start)
            {
                CompletePes(pid);
                pes[pid] = new MemoryStream();
            }
            if (!pes.TryGetValue(pid, out var buffer)) return;
            buffer.Write(payload);
            var data = buffer.GetBuffer();
            if (buffer.Length >= 6)
            {
                var length = (data[4] << 8) | data[5];
                if (length > 0 && buffer.Length >= 6 + length) CompletePes(pid);
            }
        }
    }

    private static ReadOnlySpan<byte> Section(ReadOnlySpan<byte> payload)
    {
        var pointer = payload[0];
        if (1 + pointer + 3 > payload.Length) return [];
        var section = payload[(1 + pointer)..];
        var length = ((section[1] & 0x0F) << 8) | section[2];
        return section.Length >= 3 + length ? section[..(3 + length)] : [];
    }

    private void ParsePat(ReadOnlySpan<byte> payload)
    {
        var section = Section(payload);
        if (section.Length < 12 || section[0] != 0) return;
        for (var i = 8; i + 4 <= section.Length - 4; i += 4)
        {
            var program = (section[i] << 8) | section[i + 1];
            if (program == 0) continue;
            pmtPid = ((section[i + 2] & 0x1F) << 8) | section[i + 3];
            return;
        }
    }

    private void ParsePmt(ReadOnlySpan<byte> payload)
    {
        var section = Section(payload);
        if (section.Length < 16 || section[0] != 2) return;
        var infoLength = ((section[10] & 0x0F) << 8) | section[11];
        for (var i = 12 + infoLength; i + 5 <= section.Length - 4;)
        {
            var type = section[i];
            var pid = ((section[i + 1] & 0x1F) << 8) | section[i + 2];
            var esInfo = ((section[i + 3] & 0x0F) << 8) | section[i + 4];
            if (videoPid < 0 && type is 0x1B or 0x24) { videoPid = pid; videoCodec = type == 0x1B ? VideoCodec.H264 : VideoCodec.H265; }
            if (audioPid < 0 && type == 0x0F) audioPid = pid;
            i += 5 + esInfo;
        }
    }

    private void CompletePes(int pid)
    {
        if (!pes.Remove(pid, out var buffer) || buffer.Length < 9) return;
        var data = buffer.GetBuffer().AsSpan(0, (int)buffer.Length);
        if (data[0] != 0 || data[1] != 0 || data[2] != 1) return;
        var flags = data[7];
        var headerLength = data[8];
        if (9 + headerLength > data.Length) return;
        long? pts = (flags & 0x80) != 0 ? ReadTimestamp(data[9..]) : null;
        long? dts = (flags & 0x40) != 0 ? ReadTimestamp(data[14..]) : pts;
        var declared = (data[4] << 8) | data[5];
        var end = declared > 0 ? Math.Min(data.Length, 6 + declared) : data.Length;
        var payload = data[(9 + headerLength)..end];
        if (pts is null || payload.IsEmpty) return;
        if (pid == videoPid) Video(payload, pts.Value, dts ?? pts.Value);
        else if (pid == audioPid) Audio(payload, pts.Value);
    }

    private void Video(ReadOnlySpan<byte> payload, long pts, long dts)
    {
        var data = payload.ToArray();
        var units = NalUnits.SplitAnnexB(data);
        var parameterSets = units.Select(r => data[r]).Where(n => NalUnits.IsParameterSet(videoCodec, n[0])).ToList();
        var key = units.Any(r => NalUnits.IsKeyFrame(videoCodec, data[r.Start.Value]));
        if (parameterSets.Count > 0)
        {
            try
            {
                var format = VideoFormat.FromParameterSets(videoCodec, parameterSets);
                if (videoFormat is null || !videoFormat.SameParameterSets(format)) { videoFormat = format; VideoFormatChanged?.Invoke(format); }
            }
            catch (InvalidDataException) { }
        }
        if (videoFormat is null) return;
        var dtsUs = videoClock.ToMicroseconds(dts);
        var ptsUs = dtsUs + Delta(pts, dts) * 100 / 9;
        VideoFrameReceived?.Invoke(new VideoFrame(data, ptsUs, dtsUs, key));
    }

    private void Audio(ReadOnlySpan<byte> payload, long pts)
    {
        var baseUs = audioClock.ToMicroseconds(pts);
        var index = 0;
        while (payload.Length >= 7 && Aac.TryParseAdts(payload, out var header) && header.FrameLength <= payload.Length)
        {
            if (audioFormat is null || audioFormat.SampleRate != header.SampleRate || audioFormat.Channels != header.Channels)
            {
                audioFormat = new AudioFormat(header.SampleRate, header.Channels, Aac.AudioSpecificConfig(header.SampleRate, header.Channels, header.ObjectType));
                AudioFormatChanged?.Invoke(audioFormat);
            }
            var frame = payload[header.HeaderLength..header.FrameLength].ToArray();
            AudioFrameReceived?.Invoke(new AudioFrame(frame, baseUs + index++ * Aac.SamplesPerFrame * 1_000_000L / header.SampleRate));
            payload = payload[header.FrameLength..];
        }
    }

    private static long Delta(long pts, long dts)
    {
        var delta = (pts - dts) & 0x1FFFFFFFFL;
        return delta > 0x100000000L ? delta - 0x200000000L : delta;
    }

    private static long ReadTimestamp(ReadOnlySpan<byte> data) =>
        ((long)(data[0] & 0x0E) << 29) | ((long)data[1] << 22) | ((long)(data[2] & 0xFE) << 14) | ((long)data[3] << 7) | ((long)data[4] >> 1);
}

/// <summary>Converts wrapping 33-bit 90 kHz timestamps into monotonic microseconds.</summary>
internal sealed class TimestampUnwrapper
{
    private long last = -1, offset;
    public long ToMicroseconds(long clock)
    {
        if (last >= 0 && clock < last && last - clock > 0x100000000L) offset += 0x200000000L;
        last = clock;
        return (clock + offset) * 100 / 9;
    }
}
