using System.Text;
using DeveLiveStreamer.Core;

namespace DeveLiveStreamer.App;

public static class DeviceCapabilities
{
    public static string Report(OutputSettings output)
    {
        var text = new StringBuilder();
        text.AppendLine($"Requested: {output.OutputWidth} × {output.OutputHeight} at {output.Fps:0.###} fps");
        text.AppendLine("This checks encoder claims, not a sustained streaming benchmark.\n");
#if ANDROID
        var mime = output.VideoCodec.StartsWith("H.265", StringComparison.Ordinal) ? "video/hevc" : "video/avc";
        var codecs = new Android.Media.MediaCodecList(Android.Media.MediaCodecListKind.AllCodecs).GetCodecInfos() ?? [];
        foreach (var codec in codecs.Where(c => c.IsEncoder && c.GetSupportedTypes().Contains(mime)))
        {
            try
            {
                var caps = codec.GetCapabilitiesForType(mime); var video = caps?.VideoCapabilities;
                if (video is null) continue;
                text.AppendLine(codec.Name);
                text.AppendLine(video.AreSizeAndRateSupported(output.OutputWidth, output.OutputHeight, output.Fps)
                    ? "  Requested size/FPS: advertised as supported" : "  Requested size/FPS: not advertised as supported");
                text.AppendLine($"  CBR: {caps!.EncoderCapabilities?.IsBitrateModeSupported(Android.Media.BitrateMode.Cbr)}");
                text.AppendLine($"  Width: {video.SupportedWidths} · Height: {video.SupportedHeights}");
                text.AppendLine($"  Frame rate range: {video.SupportedFrameRates}\n");
            }
            catch (Exception ex) { text.AppendLine($"  Capability query: {ex.Message}\n"); }
        }
        text.AppendLine("Camera high-speed modes:");
        var manager = (Android.Hardware.Camera2.CameraManager?)Android.App.Application.Context.GetSystemService(Android.Content.Context.CameraService);
        if (manager is not null) foreach (var id in manager.GetCameraIdList())
        {
            var map = manager.GetCameraCharacteristics(id).Get(Android.Hardware.Camera2.CameraCharacteristics.ScalerStreamConfigurationMap)
                as Android.Hardware.Camera2.Params.StreamConfigurationMap;
            if (map is null) continue;
            try
            {
                var sizes = map.GetHighSpeedVideoSizes();
                if (sizes is null || sizes.Length == 0) { text.AppendLine($"  Camera {id}: no constrained high-speed modes reported"); continue; }
                foreach (var size in sizes) text.AppendLine($"  Camera {id}: {size} · {string.Join(", ", (map.GetHighSpeedVideoFpsRangesFor(size) ?? []).Select(r => r.ToString()))}");
            }
            catch (Exception ex) { text.AppendLine($"  Camera {id}: {ex.Message}"); }
        }
#else
        text.AppendLine("Native capability discovery is currently implemented for Android.");
#endif
        return text.ToString();
    }
}
