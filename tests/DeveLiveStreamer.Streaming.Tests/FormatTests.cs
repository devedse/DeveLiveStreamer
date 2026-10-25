using DeveLiveStreamer.Streaming;
using DeveLiveStreamer.Streaming.Audio;
using DeveLiveStreamer.Streaming.Flv;
using DeveLiveStreamer.Streaming.Media;
using DeveLiveStreamer.Streaming.MpegTs;

namespace DeveLiveStreamer.Streaming.Tests;

public sealed class FormatTests
{
    // 1280x720 High profile SPS/PPS as produced by x264.
    internal static readonly byte[] Sps = Convert.FromHexString("6764001FACD9405005BB0110000003001000000303C0F1831960");
    internal static readonly byte[] Pps = Convert.FromHexString("68EBE3CB22C0");

    [Fact]
    public void AnnexBSplittingHandlesThreeAndFourByteStartCodesAndTrailingZeros()
    {
        byte[] data = [0, 0, 0, 1, 0x67, 1, 2, 0, 0, 1, 0x68, 3, 0, 0, 0, 0, 1, 0x65, 4, 5];
        var units = NalUnits.ParseAnnexB(data);
        Assert.Equal(3, units.Count);
        Assert.Equal([0x67, 1, 2], units[0]);
        Assert.Equal([0x68, 3], units[1]);
        Assert.Equal([0x65, 4, 5], units[2]);
        Assert.Equal(units, NalUnits.ParseLengthPrefixed(NalUnits.ToLengthPrefixed(units), 4));
    }

    [Fact]
    public void H264SpsDimensionsAndConfigurationRecordRoundTrip()
    {
        var sps = H264.ParseSps(Sps);
        Assert.Equal((100, 1280, 720), (sps.ProfileIdc, sps.Width, sps.Height));
        var record = H264.DecoderConfigurationRecord([Sps, Pps]);
        Assert.Equal([1, 0x64, 0x00, 0x1F, 0xFF, 0xE1], record[..6]);
        var (sets, lengthSize) = H264.ParseDecoderConfigurationRecord(record);
        Assert.Equal(4, lengthSize);
        Assert.Equal(new[] { Sps, Pps }, sets);
    }

    [Fact]
    public void AacConfigurationAndAdtsHeadersDescribeTheSameStream()
    {
        var format = AudioFormat.Aac(48000, 2);
        Assert.Equal([0x11, 0x90], format.AudioSpecificConfig);
        Span<byte> header = stackalloc byte[7];
        Aac.WriteAdtsHeader(header, format, 300);
        Assert.True(Aac.TryParseAdts(header, out var parsed));
        Assert.Equal((7, 307, 2, 48000, 2), (parsed.HeaderLength, parsed.FrameLength, parsed.ObjectType, parsed.SampleRate, parsed.Channels));
        Assert.Throws<ArgumentOutOfRangeException>(() => AudioFormat.Aac(47999, 2));
    }

    [Fact]
    public void Amf0RoundTripsCommandValues()
    {
        var encoded = Amf0.Encode("connect", 1d, new Dictionary<string, object?> { ["app"] = "live", ["fpad"] = false },
            null, new AmfEcmaArray { ["width"] = 1920d }, new List<object?> { "hvc1", "avc1" });
        var values = Amf0.Decode(encoded);
        Assert.Equal("connect", values[0]);
        Assert.Equal(1d, values[1]);
        Assert.Equal("live", ((IDictionary<string, object?>)values[2]!)["app"]);
        Assert.Null(values[3]);
        Assert.Equal(1920d, ((AmfEcmaArray)values[4]!)["width"]);
        Assert.Equal(new List<object?> { "hvc1", "avc1" }, values[5]);
    }

    [Fact]
    public void DeveStreamingPlatformSrtUrlKeepsTheOmeRouteAsStreamId()
    {
        var endpoint = Assert.IsType<SrtEndpoint>(StreamEndpoint.Parse("srt://stream.example.org:9999?streamid=default/app/my-stream"));
        Assert.Equal(("stream.example.org", 9999, "default/app/my-stream", false), (endpoint.Host, endpoint.Port, endpoint.StreamId, endpoint.Listener));
        // A URL-encoded stream ID is decoded; further options do not leak into it.
        var encoded = Assert.IsType<SrtEndpoint>(StreamEndpoint.Parse("srt://[2001:db8::1]:9000?streamid=srt%3A%2F%2Fhost%2Fapp%2Fs&latency=200000&passphrase=0123456789"));
        Assert.Equal(("2001:db8::1", "srt://host/app/s", "0123456789"), (encoded.Host, encoded.StreamId, encoded.Passphrase));
        var keyed = Assert.IsType<SrtEndpoint>(StreamEndpoint.Parse("srt://host:9999", "default/app/key"));
        Assert.Equal("default/app/key", keyed.StreamId);
        Assert.True(Assert.IsType<SrtEndpoint>(StreamEndpoint.Parse("srt://0.0.0.0:9000")).Listener);
        Assert.Throws<FormatException>(() => StreamEndpoint.Parse("srt://host"));
        Assert.Throws<FormatException>(() => StreamEndpoint.Parse("srt://host:9000?passphrase=short"));
    }

    [Fact]
    public void RtmpUrlsSplitApplicationStreamKeyAndTls()
    {
        var plain = Assert.IsType<RtmpEndpoint>(StreamEndpoint.Parse("rtmp://ome.example.org:1935/app/", "my-stream"));
        Assert.Equal(("ome.example.org", 1935, "app", "my-stream", "rtmp://ome.example.org:1935/app"), (plain.Host, plain.Port, plain.App, plain.StreamName, plain.TcUrl));
        var inline = Assert.IsType<RtmpEndpoint>(StreamEndpoint.Parse("rtmps://live.example.com/live2/abc?token=1"));
        Assert.Equal((443, true, "live2", "abc?token=1"), (inline.Port, inline.Tls, inline.App, inline.StreamName));
        Assert.Throws<FormatException>(() => StreamEndpoint.Parse("rtmp://host/app"));
        Assert.Throws<FormatException>(() => StreamEndpoint.Parse("http://host/app/key"));
    }

    [Fact]
    public void TransportStreamRoundTripsKeyFramesParameterSetsAndAudio()
    {
        var video = VideoFormat.FromParameterSets(VideoCodec.H264, [Sps, Pps], 30);
        var audio = AudioFormat.Aac(48000, 2);
        var muxer = new TsMuxer(video, audio);
        var demuxer = new TsDemuxer();
        var frames = new List<VideoFrame>(); var samples = new List<AudioFrame>();
        VideoFormat? received = null;
        demuxer.VideoFormatChanged += f => received = f;
        demuxer.VideoFrameReceived += frames.Add;
        demuxer.AudioFrameReceived += samples.Add;
        var stream = new MemoryStream();
        for (var i = 0; i < 60; i++)
        {
            var key = i % 30 == 0;
            var body = new byte[i == 0 ? 5000 : 180 + i]; // spans several packets and exercises stuffing
            body[0] = (byte)(key ? 0x65 : 0x41); body[^1] = 0x80;
            stream.Write(muxer.WriteVideo(new VideoFrame([0, 0, 0, 1, .. body], i * 33_333, i * 33_333, key)));
            stream.Write(muxer.WriteAudio(new AudioFrame(new byte[100 + i], i * 21_333)));
        }
        Assert.Equal(0, stream.Length % TsMuxer.PacketSize);
        // Deliver in SRT-sized messages to exercise packet reassembly.
        var bytes = stream.ToArray();
        for (var offset = 0; offset < bytes.Length; offset += 1000) demuxer.Push(bytes.AsSpan(offset, Math.Min(1000, bytes.Length - offset)));
        demuxer.Flush();
        Assert.Equal((1280, 720), (received!.Width, received.Height));
        Assert.Equal(60, frames.Count);
        Assert.Equal(60, samples.Count);
        Assert.Equal(2, frames.Count(f => f.IsKeyFrame));
        Assert.Equal(33_333, frames[1].DtsUs - frames[0].DtsUs, 20d);
        Assert.Equal(100 + 59, samples[^1].Data.Length);
        // Key frames carry AUD, SPS and PPS ahead of the slice.
        var units = NalUnits.ParseAnnexB(frames[30].Data);
        Assert.Equal([9, 7, 8, 5], units.Select(u => u[0] & 0x1F));
    }

    [Theory]
    [InlineData(VideoCodec.H264)]
    [InlineData(VideoCodec.H265)]
    public void FlvTagsRoundTripLegacyAvcAndEnhancedHevc(VideoCodec codec)
    {
        var format = codec == VideoCodec.H264
            ? VideoFormat.FromParameterSets(codec, [Sps, Pps])
            : VideoFormat.FromParameterSets(codec, HevcParameterSets);
        var parser = new FlvMediaParser();
        VideoFormat? parsed = null; VideoFrame? frame = null;
        parser.VideoFormatChanged += f => parsed = f;
        parser.VideoFrameReceived += f => frame = f;
        parser.Video(FlvMedia.VideoSequenceHeader(format), 0);
        byte[] slice = codec == VideoCodec.H264 ? [0x65, 0x88, 0x80] : [0x26, 0x01, 0xAF, 0x80];
        parser.Video(FlvMedia.VideoFrame(codec, new VideoFrame([0, 0, 0, 1, .. slice], 140_000, 100_000, true)), 100);
        Assert.True(parsed!.SameParameterSets(format));
        Assert.Equal((format.Width, format.Height), (parsed.Width, parsed.Height));
        Assert.Equal((100_000L, 140_000L, true), (frame!.DtsUs, frame.PtsUs, frame.IsKeyFrame));
        Assert.Equal(slice, NalUnits.ParseAnnexB(frame.Data).Single());
    }

    // 1920x1080 Main profile VPS/SPS/PPS as produced by x265.
    internal static readonly byte[][] HevcParameterSets =
    [
        Convert.FromHexString("40010C01FFFF016000000300900000030000030078959809"),
        Convert.FromHexString("420101016000000300900000030000030078A003C08010E596566924CAF0168080000003008000000F04"),
        Convert.FromHexString("4401C172B46240")
    ];

    [Fact]
    public void HevcSpsAndConfigurationRecordDescribeTheStream()
    {
        var sps = Hevc.ParseSps(HevcParameterSets[1]);
        Assert.Equal((1920, 1080, 1, 8), (sps.Width, sps.Height, sps.ChromaFormatIdc, sps.BitDepthLuma));
        var (sets, lengthSize) = Hevc.ParseDecoderConfigurationRecord(Hevc.DecoderConfigurationRecord(HevcParameterSets));
        Assert.Equal(4, lengthSize);
        Assert.Equal(HevcParameterSets, sets);
    }

    [Fact]
    public void MixerResamplesConvertsChannelsAndBoundsBufferedDelay()
    {
        var mixer = new AudioMixer(48000, 2);
        var mono = mixer.AddInput("mic");
        var other = mixer.AddInput("device", maximumBufferMs: 100);
        mono.Write(Enumerable.Repeat((short)1000, 480).ToArray(), 48000, 1);
        other.Write(Enumerable.Repeat((short)500, 441 * 2).ToArray(), 44100, 2);
        Assert.InRange(other.BufferedFrames, 478, 482);
        var output = new short[960];
        mixer.Mix(output);
        Assert.Equal(1500, output[200]);
        Assert.Equal(1500, output[201]);
        // A source that keeps writing without being mixed cannot exceed its bound.
        for (var i = 0; i < 50; i++) other.Write(new short[960], 48000, 2);
        Assert.True(other.BufferedFrames <= 4800);
        // Clipping instead of wrapping.
        var loud = mixer.AddInput("loud");
        loud.Write(Enumerable.Repeat(short.MaxValue, 960).ToArray(), 48000, 2);
        mono.Write(Enumerable.Repeat(short.MaxValue, 480).ToArray(), 48000, 1);
        mixer.Mix(output);
        Assert.Equal(short.MaxValue, output[100]);
    }
}
