using System.Globalization;
using DeveLiveStreamer.App.Studio;
using DeveLiveStreamer.Core;
using DeveLiveStreamer.Streaming;

namespace DeveLiveStreamer.App;

public sealed class SettingsPage : ContentPage
{
    private readonly Dictionary<string, Entry> fields = [];
    private readonly Dictionary<string, Picker> picks = [];

    public SettingsPage(EditorSession session)
    {
        Title = "Settings"; BackgroundColor = Theme.Background; SafeAreaEdges = SafeAreaEdges.All; HideSoftInputOnTapped = true;
        var original = session.Editor.Project.Output;
        var body = new VerticalStackLayout { Spacing = 16, Padding = 18 };
        body.Children.Add(Theme.Label("Make it your stream.", 24, bold: true));
        body.Children.Add(new Label { Text = "Familiar controls. Your device's full potential.", TextColor = Theme.Muted, FontSize = 12 });
        var panels = new Dictionary<string, VerticalStackLayout>();
        foreach (var title in new[] { "Stream", "Output", "Audio", "Video" }) panels[title] = new() { Spacing = 7, IsVisible = title == "Stream" };
        var tabs = new HorizontalStackLayout { Spacing = 6 };
        foreach (var title in panels.Keys)
        {
            var button = Theme.Button(title, () => { foreach (var panel in panels) panel.Value.IsVisible = panel.Key == title; });
            tabs.Children.Add(button);
        }
        body.Children.Add(new ScrollView { Orientation = ScrollOrientation.Horizontal, Content = tabs, HorizontalScrollBarVisibility = ScrollBarVisibility.Never });

        var stream = panels["Stream"];
        stream.Children.Add(Theme.Caption("STREAM DESTINATION"));
        stream.Children.Add(TextField("Server URL", "server", original.ServerUrl, "srt://your-server:9999?streamid=default/app/name"));
        stream.Children.Add(TextField("Stream key (optional)", "key", original.StreamKey, "For RTMP, or an SRT stream ID"));
        var interpretation = new Label { FontSize = 12, TextColor = Theme.Accent };
        void Interpret()
        {
            var url = fields["server"].Text?.Trim() ?? "";
            if (url.Length == 0) { interpretation.Text = "DeveStreamingPlatform: paste the SRT URL it shows for your stream (srt://host:9999?streamid=default/app/name)."; interpretation.TextColor = Theme.Muted; return; }
            try
            {
                interpretation.Text = StreamEndpoint.Parse(url, fields["key"].Text) switch
                {
                    SrtEndpoint srt => $"SRT to {srt.Host}:{srt.Port}" + (srt.StreamId is { Length: > 0 } id ? $"\nStream ID: {id}" : "\nNo stream ID — OvenMediaEngine needs streamid=vhost/app/stream") +
                        (srt.Passphrase is null ? "" : "\nEncrypted with the passphrase") + (srt.Listener ? "\nListener mode: a receiver must connect to this phone" : ""),
                    RtmpEndpoint rtmp => $"{(rtmp.Tls ? "RTMPS" : "RTMP")} to {rtmp.Host}:{rtmp.Port}\nApplication: {rtmp.App} · Stream: {rtmp.StreamName}",
                    _ => ""
                };
                interpretation.TextColor = Theme.Accent;
            }
            catch (FormatException ex) { interpretation.Text = ex.Message; interpretation.TextColor = Theme.Danger; }
        }
        fields["server"].TextChanged += (_, _) => Interpret();
        fields["key"].TextChanged += (_, _) => Interpret();
        Interpret();
        stream.Children.Add(interpretation);
        stream.Children.Add(Number("SRT latency (ms)", "latency", original.SrtLatencyMs, "0 is allowed. Transport buffering is separate from total stream delay; the receiver may negotiate a higher value."));
        if (StudioHost.Engine.Status.IsActive)
            stream.Children.Add(new Label { Text = "You're live: changes to destination and output settings apply to your next stream.", TextColor = Theme.Danger, FontSize = 12 });

        var output = panels["Output"];
        output.Children.Add(Theme.Caption("ENCODER SETTINGS"));
        output.Children.Add(Choice("Video encoder", "codec", original.VideoCodec, "H.264", "H.265 / HEVC"));
        output.Children.Add(Choice("Rate control", "rate", original.RateControl, "CBR", "VBR"));
        output.Children.Add(Number("Video bitrate (Kbps)", "bitrate", original.VideoBitrateKbps));
        output.Children.Add(Number("Keyframe interval (seconds)", "keyframe", original.KeyframeSeconds));
        output.Children.Add(Choice("Profile", "profile", original.Profile, "Baseline", "Main", "High"));
        output.Children.Add(Number("B-frames", "bframes", original.BFrames, "Use 0 for OvenMediaEngine WebRTC bypass compatibility."));
        output.Children.Add(Theme.Card(new Label { Text = "LOW LATENCY BASELINE\nH.264 · 1 second keyframes · 0 B-frames\nAnything the phone's encoder rejects is listed under the live status details.", FontSize = 12, TextColor = Theme.Accent }));

        var microphone = new Switch { IsToggled = original.MicrophoneEnabled, OnColor = Theme.Accent };
        var deviceAudio = new Switch { IsToggled = original.DeviceAudioEnabled, OnColor = Theme.Accent };
        var stereo = new Switch { IsToggled = original.Stereo, OnColor = Theme.Accent };
        var audio = panels["Audio"];
        audio.Children.Add(Theme.Caption("AUDIO SETTINGS"));
        audio.Children.Add(Theme.Field("Microphone", microphone));
        audio.Children.Add(Theme.Field("Device audio", deviceAudio, "Sound from other apps (Android 10+). Needs screen-capture permission; apps can opt out of being captured."));
        audio.Children.Add(Theme.Field("Stereo", stereo));
        audio.Children.Add(Number("Sample rate (Hz)", "samplerate", original.AudioSampleRate, "AAC rates: 44100 or 48000 are the usual choices."));
        audio.Children.Add(Number("Audio bitrate (Kbps)", "audiobitrate", original.AudioBitrateKbps));

        var video = panels["Video"];
        video.Children.Add(Theme.Caption("CANVAS & OUTPUT"));
        video.Children.Add(new Label { Text = "Choose a preset or enter your own values. There is no app-imposed resolution or frame-rate limit.", TextColor = Theme.Muted, FontSize = 12 });
        var presets = new HorizontalStackLayout { Spacing = 7 };
        presets.Children.Add(Theme.Button("1080p30", () => Preset(1920, 1080, 30)));
        presets.Children.Add(Theme.Button("4K60", () => Preset(3840, 2160, 60)));
        presets.Children.Add(Theme.Button("4K120", () => Preset(3840, 2160, 120)));
        video.Children.Add(presets);
        video.Children.Add(SourcePropertiesPage.Pair(Number("Base canvas width", "canvasw", original.CanvasWidth), Number("Base canvas height", "canvash", original.CanvasHeight)));
        video.Children.Add(SourcePropertiesPage.Pair(Number("Output width", "width", original.OutputWidth), Number("Output height", "height", original.OutputHeight)));
        video.Children.Add(SourcePropertiesPage.Pair(Number("FPS numerator", "fpsn", original.FpsNumerator), Number("FPS denominator", "fpsd", original.FpsDenominator)));
        video.Children.Add(new Label { Text = "Examples: 120 / 1 = 120 fps, 60000 / 1001 ≈ 59.94 fps. The native capture and encoder must support the chosen combination.", TextColor = Theme.Muted, FontSize = 11 });
        video.Children.Add(Theme.Button("Check this device's encoders", async () =>
        {
            try
            {
                var requested = ReadSettings(original); requested.Validate();
                var report = DeviceCapabilities.Report(requested);
                await Navigation.PushAsync(new ContentPage { Title = "Device capabilities", BackgroundColor = Theme.Background,
                    Content = new ScrollView { Content = new Label { Text = report, TextColor = Theme.Text, FontSize = 12,
                        Margin = 20, FontFamily = "monospace" } } });
            }
            catch (Exception ex) { await DisplayAlertAsync("Check video settings", ex.Message, "OK"); }
        }));
        foreach (var panel in panels.Values) body.Children.Add(panel);

        var actions = new Grid { RowDefinitions = new RowDefinitionCollection { new(GridLength.Auto) }, ColumnDefinitions = new ColumnDefinitionCollection { new(GridLength.Star), new(GridLength.Star) }, ColumnSpacing = 10, Padding = 16, BackgroundColor = Theme.Panel };
        actions.Add(Theme.Button("Cancel", async () => await Navigation.PopModalAsync()), 0);
        actions.Add(Theme.Button("Apply settings", async () =>
        {
            try
            {
                var settings = ReadSettings(original); settings.MicrophoneEnabled = microphone.IsToggled;
                settings.DeviceAudioEnabled = deviceAudio.IsToggled; settings.Stereo = stereo.IsToggled;
                settings.Validate();
                if (settings.ServerUrl.Length > 0)
                    settings.Protocol = StreamEndpoint.Parse(settings.ServerUrl, settings.StreamKey) switch { RtmpEndpoint { Tls: true } => "RTMPS", RtmpEndpoint => "RTMP", _ => "SRT" };
                session.Editor.Edit(p => p.Output = settings); await Navigation.PopModalAsync();
            }
            catch (Exception ex) { await DisplayAlertAsync("Check settings", ex.Message, "OK"); }
        }, true), 1);
        var root = new Grid { RowDefinitions = new RowDefinitionCollection { new(GridLength.Star), new(GridLength.Auto) } };
        root.Add(new ScrollView { Content = body }, 0, 0); root.Add(actions, 0, 1); Content = root;
    }
    private void Preset(int width, int height, int fps)
    {
        fields["canvasw"].Text = fields["width"].Text = width.ToString();
        fields["canvash"].Text = fields["height"].Text = height.ToString();
        fields["fpsn"].Text = fps.ToString(); fields["fpsd"].Text = "1";
    }
    private View Number(string label, string key, double value, string? help = null)
    {
        var entry = Theme.Entry(value.ToString(CultureInfo.InvariantCulture)); entry.Keyboard = Keyboard.Numeric;
        fields[key] = entry; return Theme.Field(label, entry, help);
    }
    private View TextField(string label, string key, string value, string? placeholder = null)
    {
        var entry = Theme.Entry(value, placeholder); entry.Keyboard = Keyboard.Url; fields[key] = entry; return Theme.Field(label, entry);
    }
    private View Choice(string label, string key, string value, params string[] options)
    {
        var picker = new Picker { TextColor = Theme.Text, BackgroundColor = Theme.Raised, HeightRequest = 46 };
        foreach (var option in options) picker.Items.Add(option); picker.SelectedItem = value;
        picks[key] = picker; return Theme.Field(label, picker);
    }
    private OutputSettings ReadSettings(OutputSettings original)
    {
        var settings = System.Text.Json.JsonSerializer.Deserialize(
            System.Text.Json.JsonSerializer.Serialize(original, ProjectJsonContext.Default.OutputSettings), ProjectJsonContext.Default.OutputSettings)!;
        settings.CanvasWidth = Int("canvasw"); settings.CanvasHeight = Int("canvash"); settings.OutputWidth = Int("width"); settings.OutputHeight = Int("height");
        settings.FpsNumerator = Int("fpsn"); settings.FpsDenominator = Int("fpsd"); settings.VideoBitrateKbps = Int("bitrate");
        settings.KeyframeSeconds = Double("keyframe"); settings.BFrames = Int("bframes"); settings.SrtLatencyMs = Int("latency");
        settings.AudioBitrateKbps = Int("audiobitrate"); settings.AudioSampleRate = Int("samplerate");
        settings.VideoCodec = (string)picks["codec"].SelectedItem; settings.Profile = (string)picks["profile"].SelectedItem;
        settings.RateControl = (string)picks["rate"].SelectedItem;
        settings.ServerUrl = fields["server"].Text?.Trim() ?? ""; settings.StreamKey = fields["key"].Text?.Trim() ?? ""; return settings;
    }
    private int Int(string key) => int.TryParse(fields[key].Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
        ? value : throw new InvalidDataException($"Enter a whole number for {key}.");
    private double Double(string key) => double.TryParse(fields[key].Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value)
        ? value : throw new InvalidDataException($"Enter a valid number for {key}.");
}
