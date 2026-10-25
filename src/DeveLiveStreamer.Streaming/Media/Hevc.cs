namespace DeveLiveStreamer.Streaming.Media;

public static class Hevc
{
    public sealed record SpsInfo(byte[] GeneralProfileTierLevel, int MaxSubLayers, bool TemporalIdNested,
        int ChromaFormatIdc, int BitDepthLuma, int BitDepthChroma, int Width, int Height);

    public static (int Width, int Height) Dimensions(byte[] sps) { var info = ParseSps(sps); return (info.Width, info.Height); }

    public static SpsInfo ParseSps(ReadOnlySpan<byte> nal)
    {
        var rbsp = NalUnits.Unescape(nal);
        var bits = new BitReader(rbsp.AsSpan(2));
        bits.Skip(4); // sps_video_parameter_set_id
        var maxSubLayersMinus1 = (int)bits.ReadBits(3);
        var nested = bits.ReadBit();
        // general_profile_space .. general_level_idc occupy exactly 12 bytes after the first 2 header bytes.
        var ptl = rbsp.AsSpan(3, 12).ToArray();
        bits.Skip(96);
        var profilePresent = new bool[maxSubLayersMinus1];
        var levelPresent = new bool[maxSubLayersMinus1];
        for (var i = 0; i < maxSubLayersMinus1; i++) { profilePresent[i] = bits.ReadBit(); levelPresent[i] = bits.ReadBit(); }
        if (maxSubLayersMinus1 > 0) for (var i = maxSubLayersMinus1; i < 8; i++) bits.Skip(2);
        for (var i = 0; i < maxSubLayersMinus1; i++)
        {
            if (profilePresent[i]) bits.Skip(88);
            if (levelPresent[i]) bits.Skip(8);
        }
        bits.ReadUe(); // sps_seq_parameter_set_id
        var chroma = (int)bits.ReadUe();
        if (chroma == 3) bits.Skip(1);
        var width = (int)bits.ReadUe();
        var height = (int)bits.ReadUe();
        if (bits.ReadBit())
        {
            var subWidth = chroma is 1 or 2 ? 2 : 1;
            var subHeight = chroma == 1 ? 2 : 1;
            int left = (int)bits.ReadUe(), right = (int)bits.ReadUe(), top = (int)bits.ReadUe(), bottom = (int)bits.ReadUe();
            width -= subWidth * (left + right);
            height -= subHeight * (top + bottom);
        }
        var depthLuma = (int)bits.ReadUe() + 8;
        var depthChroma = (int)bits.ReadUe() + 8;
        return new(ptl, maxSubLayersMinus1 + 1, nested, chroma, depthLuma, depthChroma, width, height);
    }

    /// <summary>ISO/IEC 14496-15 HEVCDecoderConfigurationRecord used by enhanced RTMP.</summary>
    public static byte[] DecoderConfigurationRecord(IReadOnlyList<byte[]> parameterSets)
    {
        var sps = parameterSets.FirstOrDefault(n => NalUnits.Type(VideoCodec.H265, n[0]) == 33)
            ?? throw new InvalidDataException("HEVC needs an SPS.");
        var info = ParseSps(sps);
        using var output = new MemoryStream();
        output.WriteByte(1);
        output.Write(info.GeneralProfileTierLevel);
        output.Write([0xF0, 0x00, 0xFC, (byte)(0xFC | info.ChromaFormatIdc),
            (byte)(0xF8 | (info.BitDepthLuma - 8)), (byte)(0xF8 | (info.BitDepthChroma - 8)), 0, 0,
            (byte)((info.MaxSubLayers << 3) | (info.TemporalIdNested ? 4 : 0) | 3)]);
        var arrays = parameterSets.GroupBy(n => NalUnits.Type(VideoCodec.H265, n[0])).Where(g => g.Key is 32 or 33 or 34).OrderBy(g => g.Key).ToList();
        output.WriteByte((byte)arrays.Count);
        foreach (var array in arrays)
        {
            output.WriteByte((byte)(0x80 | array.Key));
            var units = array.ToList();
            output.WriteByte((byte)(units.Count >> 8)); output.WriteByte((byte)units.Count);
            foreach (var unit in units) H264.WriteUnit(output, unit);
        }
        return output.ToArray();
    }

    public static (List<byte[]> ParameterSets, int LengthSize) ParseDecoderConfigurationRecord(ReadOnlySpan<byte> record)
    {
        if (record.Length < 23 || record[0] != 1) throw new InvalidDataException("Invalid HEVC configuration record.");
        var lengthSize = (record[21] & 3) + 1;
        var units = new List<byte[]>();
        var position = 23;
        for (var array = 0; array < record[22]; array++)
        {
            position++;
            var count = (record[position] << 8) | record[position + 1];
            position += 2;
            for (var i = 0; i < count; i++) units.Add(H264.ReadUnit(record, ref position));
        }
        return (units, lengthSize);
    }
}
