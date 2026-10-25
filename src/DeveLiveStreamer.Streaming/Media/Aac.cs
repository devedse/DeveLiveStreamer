namespace DeveLiveStreamer.Streaming.Media;

public static class Aac
{
    public const int SamplesPerFrame = 1024;
    private static readonly int[] Rates = [96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350];

    public static bool IsStandardRate(int sampleRate) => Array.IndexOf(Rates, sampleRate) >= 0;
    public static int FrequencyIndex(int sampleRate) => Array.IndexOf(Rates, sampleRate) is var index and >= 0
        ? index : throw new ArgumentOutOfRangeException(nameof(sampleRate), $"AAC does not define a {sampleRate} Hz sampling rate.");

    /// <summary>AAC-LC AudioSpecificConfig (ISO/IEC 14496-3).</summary>
    public static byte[] AudioSpecificConfig(int sampleRate, int channels, int objectType = 2)
    {
        var index = FrequencyIndex(sampleRate);
        var value = (objectType << 11) | (index << 7) | (channels << 3);
        return [(byte)(value >> 8), (byte)value];
    }

    public static (int ObjectType, int SampleRate, int Channels) ParseAudioSpecificConfig(ReadOnlySpan<byte> config)
    {
        if (config.Length < 2) throw new InvalidDataException("The AAC configuration is too short.");
        var bits = new BitReader(config);
        var objectType = (int)bits.ReadBits(5);
        if (objectType == 31) objectType = 32 + (int)bits.ReadBits(6);
        var index = (int)bits.ReadBits(4);
        var rate = index == 15 ? (int)bits.ReadBits(24) : index < Rates.Length ? Rates[index] : throw new InvalidDataException("Invalid AAC sampling index.");
        var channels = (int)bits.ReadBits(4);
        return (objectType, rate, channels);
    }

    public static void WriteAdtsHeader(Span<byte> header, AudioFormat format, int payloadLength)
    {
        var (objectType, rate, channels) = ParseAudioSpecificConfig(format.AudioSpecificConfig);
        var index = FrequencyIndex(rate);
        var length = payloadLength + 7;
        header[0] = 0xFF;
        header[1] = 0xF1; // MPEG-4, layer 0, no CRC
        header[2] = (byte)(((objectType - 1) << 6) | (index << 2) | (channels >> 2));
        header[3] = (byte)(((channels & 3) << 6) | (length >> 11));
        header[4] = (byte)(length >> 3);
        header[5] = (byte)(((length & 7) << 5) | 0x1F);
        header[6] = 0xFC;
    }

    public readonly record struct AdtsHeader(int HeaderLength, int FrameLength, int ObjectType, int SampleRate, int Channels);

    public static bool TryParseAdts(ReadOnlySpan<byte> data, out AdtsHeader header)
    {
        header = default;
        if (data.Length < 7 || data[0] != 0xFF || (data[1] & 0xF0) != 0xF0) return false;
        var headerLength = (data[1] & 1) == 1 ? 7 : 9;
        var objectType = ((data[2] >> 6) & 3) + 1;
        var index = (data[2] >> 2) & 0xF;
        if (index >= Rates.Length) return false;
        var channels = ((data[2] & 1) << 2) | (data[3] >> 6);
        var frameLength = ((data[3] & 3) << 11) | (data[4] << 3) | (data[5] >> 5);
        if (frameLength < headerLength) return false;
        header = new(headerLength, frameLength, objectType, Rates[index], channels);
        return true;
    }
}
