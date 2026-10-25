using DeveLiveStreamer.Streaming.Media;

namespace DeveLiveStreamer.Streaming.MpegTs;

/// <summary>
/// MPEG-2 transport stream muxer for H.264/HEVC and ADTS AAC, as accepted by OvenMediaEngine's SRT provider.
/// Each call returns whole 188-byte packets for exactly one access unit.
/// </summary>
public sealed class TsMuxer
{
    public const int PacketSize = 188;
    internal const int PmtPid = 0x1000, VideoPid = 0x100, AudioPid = 0x101;
    // Timestamps are offset so PCR (taken from DTS) stays ahead of every decode time.
    private const long TimestampOffset = 63_000;
    private const long PsiIntervalUs = 500_000;
    private readonly VideoFormat? video;
    private readonly AudioFormat? audio;
    private readonly byte[] adts = new byte[7];
    private readonly Dictionary<int, int> continuity = [];
    private long lastPsiUs = long.MinValue;

    public TsMuxer(VideoFormat? video, AudioFormat? audio)
    {
        if (video is null && audio is null) throw new ArgumentException("A transport stream needs at least one track.");
        this.video = video; this.audio = audio;
    }
    private int PcrPid => video is not null ? VideoPid : AudioPid;

    public byte[] WriteVideo(VideoFrame frame)
    {
        if (video is null) throw new InvalidOperationException("This muxer has no video track.");
        using var output = new MemoryStream(frame.Data.Length + 1024);
        if (frame.IsKeyFrame || frame.DtsUs - lastPsiUs >= PsiIntervalUs || lastPsiUs == long.MinValue) WritePsi(output, frame.DtsUs);
        var payload = VideoPayload(frame);
        var pts = ToClock(frame.PtsUs); var dts = ToClock(frame.DtsUs);
        var pes = Pes(0xE0, payload, pts, dts == pts ? null : dts, unbounded: true);
        Packetize(output, VideoPid, pes, dts - TimestampOffset, frame.IsKeyFrame);
        return output.ToArray();
    }

    public byte[] WriteAudio(AudioFrame frame)
    {
        if (audio is null) throw new InvalidOperationException("This muxer has no audio track.");
        using var output = new MemoryStream(frame.Data.Length + 512);
        if (lastPsiUs == long.MinValue || (video is null && frame.PtsUs - lastPsiUs >= PsiIntervalUs)) WritePsi(output, frame.PtsUs);
        Aac.WriteAdtsHeader(adts, audio, frame.Data.Length);
        var payload = new byte[7 + frame.Data.Length];
        adts.CopyTo(payload, 0); frame.Data.CopyTo(payload, 7);
        var pts = ToClock(frame.PtsUs);
        Packetize(output, AudioPid, Pes(0xC0, payload, pts, null, unbounded: false), video is null ? pts - TimestampOffset : null, false);
        return output.ToArray();
    }

    private byte[] VideoPayload(VideoFrame frame)
    {
        var codec = video!.Codec;
        var units = NalUnits.SplitAnnexB(frame.Data);
        var hasParameterSets = units.Any(r => NalUnits.IsSps(codec, frame.Data[r.Start.Value]));
        using var output = new MemoryStream(frame.Data.Length + 256);
        output.Write(codec == VideoCodec.H264 ? [0, 0, 0, 1, 0x09, 0xF0] : [0, 0, 0, 1, 0x46, 0x01, 0x50]);
        if (frame.IsKeyFrame && !hasParameterSets)
            foreach (var set in video.ParameterSets) { output.Write([0, 0, 0, 1]); output.Write(set); }
        foreach (var range in units)
        {
            var unit = frame.Data.AsSpan(range);
            if (NalUnits.IsAccessUnitDelimiter(codec, unit[0])) continue;
            output.Write([0, 0, 0, 1]); output.Write(unit);
        }
        return output.ToArray();
    }

    private static long ToClock(long microseconds) => (microseconds * 9 / 100 + TimestampOffset) & 0x1FFFFFFFFL;

    private static byte[] Pes(byte streamId, byte[] payload, long pts, long? dts, bool unbounded)
    {
        var headerData = dts is null ? 5 : 10;
        var pes = new byte[9 + headerData + payload.Length];
        pes[2] = 1; pes[3] = streamId;
        var length = 3 + headerData + payload.Length;
        if (!unbounded || length <= 0xFFFF) { pes[4] = (byte)(length >> 8); pes[5] = (byte)length; }
        if (unbounded && length > 0xFFFF) pes[4] = pes[5] = 0;
        pes[6] = 0x84; // marker bits, data_alignment_indicator
        pes[7] = (byte)(dts is null ? 0x80 : 0xC0);
        pes[8] = (byte)headerData;
        WriteTimestamp(pes.AsSpan(9), dts is null ? 0x2 : 0x3, pts);
        if (dts is not null) WriteTimestamp(pes.AsSpan(14), 0x1, dts.Value);
        payload.CopyTo(pes, 9 + headerData);
        return pes;
    }

    private static void WriteTimestamp(Span<byte> target, int prefix, long value)
    {
        target[0] = (byte)((prefix << 4) | (int)((value >> 29) & 0x0E) | 1);
        target[1] = (byte)(value >> 22);
        target[2] = (byte)(((value >> 14) & 0xFE) | 1);
        target[3] = (byte)(value >> 7);
        target[4] = (byte)(((value << 1) & 0xFE) | 1);
    }

    private void Packetize(Stream output, int pid, ReadOnlySpan<byte> pes, long? pcr, bool randomAccess)
    {
        Span<byte> packet = stackalloc byte[PacketSize];
        var first = true;
        while (pes.Length > 0)
        {
            var hasPcr = first && pcr is not null && pid == PcrPid;
            var flags = first && randomAccess;
            var adaptationBody = hasPcr ? 7 : flags ? 1 : 0; // flags byte + optional PCR
            var adaptation = adaptationBody > 0 ? 1 + adaptationBody : 0;
            var capacity = PacketSize - 4 - adaptation;
            if (pes.Length < capacity)
            {
                var stuffing = capacity - pes.Length;
                if (adaptation == 0) { adaptation = stuffing; adaptationBody = Math.Max(0, stuffing - 1); }
                else { adaptation += stuffing; adaptationBody += stuffing; }
                capacity = pes.Length;
            }
            packet.Fill(0xFF);
            packet[0] = 0x47;
            packet[1] = (byte)((first ? 0x40 : 0) | ((pid >> 8) & 0x1F));
            packet[2] = (byte)pid;
            packet[3] = (byte)((adaptation > 0 ? 0x30 : 0x10) | NextContinuity(pid));
            var offset = 4;
            if (adaptation > 0)
            {
                packet[offset++] = (byte)adaptationBody;
                if (adaptationBody > 0)
                {
                    packet[offset] = (byte)((flags ? 0x40 : 0) | (hasPcr ? 0x10 : 0));
                    if (hasPcr) WritePcr(packet.Slice(offset + 1), pcr!.Value & 0x1FFFFFFFFL);
                }
                offset = 4 + adaptation;
            }
            pes[..capacity].CopyTo(packet[offset..]);
            pes = pes[capacity..];
            output.Write(packet);
            first = false;
        }
    }

    private static void WritePcr(Span<byte> target, long pcrBase)
    {
        target[0] = (byte)(pcrBase >> 25);
        target[1] = (byte)(pcrBase >> 17);
        target[2] = (byte)(pcrBase >> 9);
        target[3] = (byte)(pcrBase >> 1);
        target[4] = (byte)(((pcrBase & 1) << 7) | 0x7E);
        target[5] = 0;
    }

    private void WritePsi(Stream output, long timeUs)
    {
        lastPsiUs = timeUs;
        WriteSection(output, 0, [0x00, 0x01, 0xC1, 0x00, 0x00, 0x00, 0x01, (byte)(0xE0 | (PmtPid >> 8)), PmtPid & 0xFF], 0x00);
        var pmt = new List<byte> { 0x00, 0x01, 0xC1, 0x00, 0x00, (byte)(0xE0 | (PcrPid >> 8)), (byte)PcrPid, 0xF0, 0x00 };
        if (video is not null) pmt.AddRange([(byte)(video.Codec == VideoCodec.H264 ? 0x1B : 0x24), 0xE0 | (VideoPid >> 8), VideoPid & 0xFF, 0xF0, 0x00]);
        if (audio is not null) pmt.AddRange([0x0F, 0xE0 | (AudioPid >> 8), AudioPid & 0xFF, 0xF0, 0x00]);
        WriteSection(output, PmtPid, pmt.ToArray(), 0x02);
    }

    private void WriteSection(Stream output, int pid, byte[] body, byte tableId)
    {
        // body starts at the table_id_extension (transport_stream_id / program_number).
        var sectionLength = body.Length + 4;
        var section = new byte[3 + body.Length + 4];
        section[0] = tableId;
        section[1] = (byte)(0xB0 | (sectionLength >> 8));
        section[2] = (byte)sectionLength;
        body.CopyTo(section, 3);
        var crc = Crc32Mpeg.Compute(section.AsSpan(0, section.Length - 4));
        section[^4] = (byte)(crc >> 24); section[^3] = (byte)(crc >> 16); section[^2] = (byte)(crc >> 8); section[^1] = (byte)crc;
        Span<byte> packet = stackalloc byte[PacketSize];
        packet.Fill(0xFF);
        packet[0] = 0x47; packet[1] = (byte)(0x40 | (pid >> 8)); packet[2] = (byte)pid;
        packet[3] = (byte)(0x10 | NextContinuity(pid));
        packet[4] = 0; // pointer_field
        section.CopyTo(packet[5..]);
        output.Write(packet);
    }

    private int NextContinuity(int pid)
    {
        continuity.TryGetValue(pid, out var value);
        continuity[pid] = (value + 1) & 0xF;
        return value;
    }
}

internal static class Crc32Mpeg
{
    private static readonly uint[] Table = Enumerable.Range(0, 256).Select(i =>
    {
        var crc = (uint)i << 24;
        for (var bit = 0; bit < 8; bit++) crc = (crc & 0x80000000) != 0 ? (crc << 1) ^ 0x04C11DB7 : crc << 1;
        return crc;
    }).ToArray();

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFF;
        foreach (var value in data) crc = (crc << 8) ^ Table[((crc >> 24) ^ value) & 0xFF];
        return crc;
    }
}
