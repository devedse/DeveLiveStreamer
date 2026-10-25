namespace DeveLiveStreamer.Streaming.Media;

public static class H264
{
    private static readonly int[] HighProfiles = [100, 110, 122, 244, 44, 83, 86, 118, 128, 138, 139, 134, 135];

    public sealed record SpsInfo(int ProfileIdc, int ChromaFormatIdc, int BitDepthLuma, int BitDepthChroma, int Width, int Height);

    public static (int Width, int Height) Dimensions(byte[] sps) { var info = ParseSps(sps); return (info.Width, info.Height); }

    public static SpsInfo ParseSps(ReadOnlySpan<byte> nal)
    {
        var rbsp = NalUnits.Unescape(nal);
        var bits = new BitReader(rbsp.AsSpan(1));
        var profile = (int)bits.ReadBits(8);
        bits.Skip(16); // constraint flags and level
        bits.ReadUe(); // seq_parameter_set_id
        int chroma = 1, depthLuma = 8, depthChroma = 8;
        if (HighProfiles.Contains(profile))
        {
            chroma = (int)bits.ReadUe();
            if (chroma == 3) bits.Skip(1);
            depthLuma = (int)bits.ReadUe() + 8;
            depthChroma = (int)bits.ReadUe() + 8;
            bits.Skip(1);
            if (bits.ReadBit())
                for (var i = 0; i < (chroma != 3 ? 8 : 12); i++)
                    if (bits.ReadBit()) SkipScalingList(ref bits, i < 6 ? 16 : 64);
        }
        bits.ReadUe(); // log2_max_frame_num_minus4
        var pocType = bits.ReadUe();
        if (pocType == 0) bits.ReadUe();
        else if (pocType == 1)
        {
            bits.Skip(1); bits.ReadSe(); bits.ReadSe();
            var cycle = bits.ReadUe();
            for (var i = 0; i < cycle; i++) bits.ReadSe();
        }
        bits.ReadUe(); bits.Skip(1); // max_num_ref_frames, gaps
        var widthMbs = (int)bits.ReadUe() + 1;
        var heightUnits = (int)bits.ReadUe() + 1;
        var frameMbsOnly = bits.ReadBit();
        if (!frameMbsOnly) bits.Skip(1);
        bits.Skip(1);
        int left = 0, right = 0, top = 0, bottom = 0;
        if (bits.ReadBit()) { left = (int)bits.ReadUe(); right = (int)bits.ReadUe(); top = (int)bits.ReadUe(); bottom = (int)bits.ReadUe(); }
        var cropX = chroma == 0 ? 1 : chroma == 3 ? 1 : 2;
        var cropY = (chroma == 0 ? 1 : chroma == 1 ? 2 : 1) * (frameMbsOnly ? 1 : 2);
        var width = widthMbs * 16 - cropX * (left + right);
        var height = (frameMbsOnly ? 1 : 2) * heightUnits * 16 - cropY * (top + bottom);
        return new(profile, chroma, depthLuma, depthChroma, width, height);
    }

    /// <summary>ISO/IEC 14496-15 AVCDecoderConfigurationRecord used by FLV/RTMP.</summary>
    public static byte[] DecoderConfigurationRecord(IReadOnlyList<byte[]> parameterSets)
    {
        var sps = parameterSets.Where(n => NalUnits.Type(VideoCodec.H264, n[0]) == 7).ToList();
        var pps = parameterSets.Where(n => NalUnits.Type(VideoCodec.H264, n[0]) == 8).ToList();
        if (sps.Count == 0 || pps.Count == 0) throw new InvalidDataException("H.264 needs both SPS and PPS.");
        using var output = new MemoryStream();
        output.Write([1, sps[0][1], sps[0][2], sps[0][3], 0xFF, (byte)(0xE0 | sps.Count)]);
        foreach (var unit in sps) WriteUnit(output, unit);
        output.WriteByte((byte)pps.Count);
        foreach (var unit in pps) WriteUnit(output, unit);
        var info = ParseSps(sps[0]);
        if (HighProfiles.Contains(info.ProfileIdc))
            output.Write([(byte)(0xFC | info.ChromaFormatIdc), (byte)(0xF8 | (info.BitDepthLuma - 8)), (byte)(0xF8 | (info.BitDepthChroma - 8)), 0]);
        return output.ToArray();
    }

    public static (List<byte[]> ParameterSets, int LengthSize) ParseDecoderConfigurationRecord(ReadOnlySpan<byte> record)
    {
        if (record.Length < 7 || record[0] != 1) throw new InvalidDataException("Invalid AVC configuration record.");
        var lengthSize = (record[4] & 3) + 1;
        var units = new List<byte[]>();
        var position = 5;
        var spsCount = record[position++] & 0x1F;
        for (var i = 0; i < spsCount; i++) units.Add(ReadUnit(record, ref position));
        var ppsCount = record[position++];
        for (var i = 0; i < ppsCount; i++) units.Add(ReadUnit(record, ref position));
        return (units, lengthSize);
    }

    internal static void WriteUnit(Stream output, byte[] unit)
    {
        output.WriteByte((byte)(unit.Length >> 8)); output.WriteByte((byte)unit.Length); output.Write(unit);
    }
    internal static byte[] ReadUnit(ReadOnlySpan<byte> record, ref int position)
    {
        var length = (record[position] << 8) | record[position + 1];
        position += 2;
        if (position + length > record.Length) throw new InvalidDataException("A configuration record is truncated.");
        var unit = record.Slice(position, length).ToArray();
        position += length;
        return unit;
    }
    private static void SkipScalingList(ref BitReader bits, int size)
    {
        int last = 8, next = 8;
        for (var j = 0; j < size; j++)
        {
            if (next != 0) next = (last + bits.ReadSe() + 256) % 256;
            last = next == 0 ? last : next;
        }
    }
}
