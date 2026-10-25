using System.Buffers.Binary;

namespace DeveLiveStreamer.Streaming.Media;

public static class NalUnits
{
    private static readonly byte[] StartCode = [0, 0, 0, 1];

    /// <summary>Ranges of NAL units in an Annex-B buffer, excluding start codes and trailing zero bytes.</summary>
    public static List<Range> SplitAnnexB(ReadOnlySpan<byte> data)
    {
        ReadOnlySpan<byte> code = [0, 0, 1];
        var units = new List<Range>();
        int start = -1, position = 0;
        while (position < data.Length)
        {
            var found = data[position..].IndexOf(code);
            if (found < 0) break;
            var at = position + found;
            if (start >= 0) units.Add(start..TrimZeros(data, start, at));
            start = position = at + 3;
        }
        if (start >= 0 && start < data.Length) units.Add(start..data.Length);
        return units.Where(r => r.End.Value > r.Start.Value).ToList();
    }

    public static List<byte[]> ParseAnnexB(ReadOnlySpan<byte> data)
    {
        var result = new List<byte[]>();
        foreach (var range in SplitAnnexB(data)) result.Add(data[range].ToArray());
        return result;
    }

    public static List<byte[]> ParseLengthPrefixed(ReadOnlySpan<byte> data, int lengthSize)
    {
        var result = new List<byte[]>();
        var position = 0;
        while (position + lengthSize <= data.Length)
        {
            var length = 0;
            for (var i = 0; i < lengthSize; i++) length = (length << 8) | data[position + i];
            position += lengthSize;
            if (length <= 0 || position + length > data.Length) break;
            result.Add(data.Slice(position, length).ToArray());
            position += length;
        }
        return result;
    }

    public static byte[] ToAnnexB(IEnumerable<byte[]> units)
    {
        using var output = new MemoryStream();
        foreach (var unit in units) { output.Write(StartCode); output.Write(unit); }
        return output.ToArray();
    }

    public static byte[] ToLengthPrefixed(IEnumerable<byte[]> units)
    {
        using var output = new MemoryStream();
        Span<byte> length = stackalloc byte[4];
        foreach (var unit in units)
        {
            BinaryPrimitives.WriteInt32BigEndian(length, unit.Length);
            output.Write(length); output.Write(unit);
        }
        return output.ToArray();
    }

    public static int Type(VideoCodec codec, byte header) => codec == VideoCodec.H264 ? header & 0x1F : (header >> 1) & 0x3F;
    public static bool IsSps(VideoCodec codec, byte header) => Type(codec, header) == (codec == VideoCodec.H264 ? 7 : 33);
    public static bool IsParameterSet(VideoCodec codec, byte header) => codec == VideoCodec.H264
        ? Type(codec, header) is 7 or 8 : Type(codec, header) is 32 or 33 or 34;
    public static bool IsKeyFrame(VideoCodec codec, byte header) => codec == VideoCodec.H264
        ? Type(codec, header) == 5 : Type(codec, header) is >= 16 and <= 21;
    public static bool IsAccessUnitDelimiter(VideoCodec codec, byte header) => Type(codec, header) == (codec == VideoCodec.H264 ? 9 : 35);

    /// <summary>Removes emulation-prevention bytes (00 00 03 → 00 00) to obtain the RBSP.</summary>
    public static byte[] Unescape(ReadOnlySpan<byte> nal)
    {
        var output = new byte[nal.Length];
        int count = 0, zeros = 0;
        foreach (var value in nal)
        {
            if (zeros >= 2 && value == 3) { zeros = 0; continue; }
            output[count++] = value;
            zeros = value == 0 ? zeros + 1 : 0;
        }
        return output[..count];
    }

    private static int TrimZeros(ReadOnlySpan<byte> data, int start, int end)
    {
        while (end > start && data[end - 1] == 0) end--;
        return end;
    }
}

internal ref struct BitReader(ReadOnlySpan<byte> data)
{
    private readonly ReadOnlySpan<byte> data = data;
    private int position;

    public bool ReadBit()
    {
        if (position >= data.Length * 8) throw new InvalidDataException("A parameter set ended unexpectedly.");
        var bit = (data[position >> 3] >> (7 - (position & 7))) & 1;
        position++;
        return bit == 1;
    }
    public uint ReadBits(int count)
    {
        uint value = 0;
        for (var i = 0; i < count; i++) value = (value << 1) | (ReadBit() ? 1u : 0u);
        return value;
    }
    public void Skip(int count) { for (var i = 0; i < count; i++) ReadBit(); }
    public uint ReadUe()
    {
        var zeros = 0;
        while (!ReadBit()) if (++zeros > 31) throw new InvalidDataException("Invalid Exp-Golomb value.");
        return zeros == 0 ? 0 : (1u << zeros) - 1 + ReadBits(zeros);
    }
    public int ReadSe()
    {
        var value = ReadUe();
        return (value & 1) == 1 ? (int)((value + 1) / 2) : -(int)(value / 2);
    }
}
