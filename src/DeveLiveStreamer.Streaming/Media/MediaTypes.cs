namespace DeveLiveStreamer.Streaming.Media;

public enum VideoCodec { H264, H265 }

/// <summary>One encoded access unit in Annex-B form. Timestamps are microseconds on the session clock.</summary>
public sealed record VideoFrame(byte[] Data, long PtsUs, long DtsUs, bool IsKeyFrame);

/// <summary>One raw AAC frame without ADTS framing.</summary>
public sealed record AudioFrame(byte[] Data, long PtsUs);

/// <summary>Parameter sets are NAL units without start codes: VPS (HEVC), SPS and PPS.</summary>
public sealed record VideoFormat(VideoCodec Codec, IReadOnlyList<byte[]> ParameterSets, int Width, int Height, double FrameRate = 0)
{
    public static VideoFormat FromParameterSets(VideoCodec codec, IReadOnlyList<byte[]> parameterSets, double frameRate = 0)
    {
        var sps = parameterSets.FirstOrDefault(n => n.Length > 0 && NalUnits.IsSps(codec, n[0]))
            ?? throw new InvalidDataException("The video stream has no sequence parameter set.");
        var (width, height) = codec == VideoCodec.H264 ? H264.Dimensions(sps) : Hevc.Dimensions(sps);
        return new(codec, parameterSets, width, height, frameRate);
    }
    public bool SameParameterSets(VideoFormat other) => Codec == other.Codec &&
        ParameterSets.Count == other.ParameterSets.Count && ParameterSets.Zip(other.ParameterSets).All(p => p.First.AsSpan().SequenceEqual(p.Second));
}

public sealed record AudioFormat(int SampleRate, int Channels, byte[] AudioSpecificConfig)
{
    public static AudioFormat Aac(int sampleRate, int channels) => new(sampleRate, channels, Media.Aac.AudioSpecificConfig(sampleRate, channels));
    public static AudioFormat FromAudioSpecificConfig(byte[] config)
    {
        var (_, rate, channels) = Media.Aac.ParseAudioSpecificConfig(config);
        return new(rate, channels, config);
    }
}

/// <summary>Common events of demuxers and incoming stream clients/servers.</summary>
public interface IMediaReceiver
{
    event Action<VideoFormat>? VideoFormatChanged;
    event Action<VideoFrame>? VideoFrameReceived;
    event Action<AudioFormat>? AudioFormatChanged;
    event Action<AudioFrame>? AudioFrameReceived;
}
