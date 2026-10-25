using DeveLiveStreamer.Streaming.Media;

namespace DeveLiveStreamer.Streaming.Flv;

/// <summary>Builds FLV audio/video tag bodies (the payloads of RTMP message types 8 and 9).</summary>
public static class FlvMedia
{
    private static readonly byte[] Hvc1 = "hvc1"u8.ToArray();
    private const int SequenceStart = 0, CodedFrames = 1;

    public static byte[] VideoSequenceHeader(VideoFormat format) => format.Codec == VideoCodec.H264
        ? [0x17, 0, 0, 0, 0, .. H264.DecoderConfigurationRecord(format.ParameterSets)]
        // Enhanced RTMP: IsExHeader | key frame | PacketTypeSequenceStart, FourCC hvc1.
        : [(byte)(0x80 | (1 << 4) | SequenceStart), .. Hvc1, .. Hevc.DecoderConfigurationRecord(format.ParameterSets)];

    public static byte[] VideoFrame(VideoCodec codec, VideoFrame frame)
    {
        var units = NalUnits.SplitAnnexB(frame.Data).Select(r => frame.Data[r]).Where(n =>
            !NalUnits.IsAccessUnitDelimiter(codec, n[0]) && !NalUnits.IsParameterSet(codec, n[0]));
        var body = NalUnits.ToLengthPrefixed(units);
        var composition = (int)((frame.PtsUs - frame.DtsUs) / 1000);
        var frameType = frame.IsKeyFrame ? 1 : 2;
        byte c0 = (byte)(composition >> 16), c1 = (byte)(composition >> 8), c2 = (byte)composition;
        return codec == VideoCodec.H264
            ? [(byte)((frameType << 4) | 7), 1, c0, c1, c2, .. body]
            : [(byte)(0x80 | (frameType << 4) | CodedFrames), .. Hvc1, c0, c1, c2, .. body];
    }

    public static byte[] AudioSequenceHeader(AudioFormat format) => [0xAF, 0, .. format.AudioSpecificConfig];
    public static byte[] AudioFrame(AudioFrame frame) => [0xAF, 1, .. frame.Data];
}

/// <summary>Parses FLV/RTMP media messages, including legacy and enhanced-RTMP HEVC, into Annex-B frames.</summary>
public sealed class FlvMediaParser : IMediaReceiver
{
    private int lengthSize = 4;
    private VideoFormat? videoFormat;

    public event Action<VideoFormat>? VideoFormatChanged;
    public event Action<VideoFrame>? VideoFrameReceived;
    public event Action<AudioFormat>? AudioFormatChanged;
    public event Action<AudioFrame>? AudioFrameReceived;

    /// <summary>Handles one video message; malformed messages are skipped rather than ending the stream.</summary>
    public void Video(ReadOnlySpan<byte> body, long timestampMs)
    {
        try { ParseVideo(body, timestampMs); }
        catch (Exception ex) when (ex is InvalidDataException or IndexOutOfRangeException or ArgumentOutOfRangeException) { }
    }

    private void ParseVideo(ReadOnlySpan<byte> body, long timestampMs)
    {
        if (body.Length < 5) return;
        VideoCodec codec; int packetType, frameType, dataOffset, composition = 0;
        if ((body[0] & 0x80) != 0)
        {
            frameType = (body[0] >> 4) & 7; packetType = body[0] & 0x0F;
            var fourCc = body[1..5];
            if (fourCc.SequenceEqual("hvc1"u8)) codec = VideoCodec.H265;
            else if (fourCc.SequenceEqual("avc1"u8)) codec = VideoCodec.H264;
            else return;
            dataOffset = 5;
            if (packetType == 1 && body.Length >= 8) { composition = ReadSigned24(body[5..]); dataOffset = 8; }
            else if (packetType == 3) packetType = 1;
            else if (packetType != 0) return;
        }
        else
        {
            frameType = body[0] >> 4;
            var codecId = body[0] & 0x0F;
            if (codecId is not (7 or 12)) return;
            codec = codecId == 7 ? VideoCodec.H264 : VideoCodec.H265;
            packetType = body[1];
            if (packetType > 1) return;
            composition = ReadSigned24(body[2..]);
            dataOffset = 5;
        }
        var data = body[dataOffset..];
        if (packetType == 0)
        {
            var (parameterSets, size) = codec == VideoCodec.H264 ? H264.ParseDecoderConfigurationRecord(data) : Hevc.ParseDecoderConfigurationRecord(data);
            lengthSize = size;
            var format = VideoFormat.FromParameterSets(codec, parameterSets);
            if (videoFormat is null || !videoFormat.SameParameterSets(format)) { videoFormat = format; VideoFormatChanged?.Invoke(format); }
            return;
        }
        if (videoFormat is null) return;
        var units = NalUnits.ParseLengthPrefixed(data, lengthSize);
        if (units.Count == 0) return;
        var dts = timestampMs * 1000;
        var key = frameType == 1 || units.Any(u => NalUnits.IsKeyFrame(codec, u[0]));
        VideoFrameReceived?.Invoke(new VideoFrame(NalUnits.ToAnnexB(units), dts + composition * 1000L, dts, key));
    }

    public void Audio(ReadOnlySpan<byte> body, long timestampMs)
    {
        try { ParseAudio(body, timestampMs); }
        catch (Exception ex) when (ex is InvalidDataException or IndexOutOfRangeException or ArgumentOutOfRangeException) { }
    }

    private void ParseAudio(ReadOnlySpan<byte> body, long timestampMs)
    {
        if (body.Length < 2 || body[0] >> 4 != 10) return;
        if (body[1] == 0)
        {
            // FFmpeg announces an empty configuration before the real one when copying ADTS input.
            if (body.Length < 4) return;
            var format = AudioFormat.FromAudioSpecificConfig(body[2..].ToArray());
            AudioFormatChanged?.Invoke(format);
        }
        else AudioFrameReceived?.Invoke(new AudioFrame(body[2..].ToArray(), timestampMs * 1000));
    }

    private static int ReadSigned24(ReadOnlySpan<byte> value)
    {
        var raw = (value[0] << 16) | (value[1] << 8) | value[2];
        return (raw & 0x800000) != 0 ? raw - 0x1000000 : raw;
    }
}
