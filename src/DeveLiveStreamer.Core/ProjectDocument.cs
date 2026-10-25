using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeveLiveStreamer.Core;

public enum SourceKind { Camera, Text, Image, Color, Screen, Network }

public sealed class SourceDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Source";
    public SourceKind Kind { get; set; }
    public string Content { get; set; } = "";
    public string Color { get; set; } = "#F3F6FA";
    public string BackgroundColor { get; set; } = "#14232D";
    public double FontSize { get; set; } = 72;
    public bool FrontCamera { get; set; }
    // Empty retains the legacy automatic front/back selection in existing collections.
    public string CameraId { get; set; } = "";
    // Flips video horizontally in the output (e.g. to un-mirror a front camera).
    public bool Mirror { get; set; }
    // For network streams: mix the incoming audio into the output.
    public bool AudioEnabled { get; set; } = true;
}

public sealed class SceneItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string SourceId { get; set; } = "";
    // Normalized canvas coordinates permit independent canvas/output resolutions.
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; } = 1;
    public double Height { get; set; } = 1;
    public double Opacity { get; set; } = 1;
    public bool Visible { get; set; } = true;
    public bool Locked { get; set; }
}

public sealed class Scene
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Scene";
    // Items are stored bottom-to-top, just like drawing order.
    public List<SceneItem> Items { get; set; } = [];
}

public sealed class OutputSettings
{
    public int CanvasWidth { get; set; } = 1920;
    public int CanvasHeight { get; set; } = 1080;
    public int OutputWidth { get; set; } = 1920;
    public int OutputHeight { get; set; } = 1080;
    public int FpsNumerator { get; set; } = 30;
    public int FpsDenominator { get; set; } = 1;
    public string VideoCodec { get; set; } = "H.264";
    public string RateControl { get; set; } = "CBR";
    public int VideoBitrateKbps { get; set; } = 6000;
    public double KeyframeSeconds { get; set; } = 1;
    public int BFrames { get; set; }
    public string Profile { get; set; } = "High";
    public int SrtLatencyMs { get; set; }
    public string Protocol { get; set; } = "SRT";
    public string ServerUrl { get; set; } = "";
    // Optional: appended to RTMP URLs, or used as the SRT stream ID when the URL has none.
    public string StreamKey { get; set; } = "";
    public int AudioBitrateKbps { get; set; } = 128;
    public int AudioSampleRate { get; set; } = 48000;
    public bool MicrophoneEnabled { get; set; } = true;
    public bool DeviceAudioEnabled { get; set; }
    public bool Stereo { get; set; } = true;
    [JsonIgnore] public double Fps => (double)FpsNumerator / FpsDenominator;

    public void Validate()
    {
        if (CanvasWidth <= 0 || CanvasHeight <= 0 || OutputWidth <= 0 || OutputHeight <= 0)
            throw new InvalidDataException("Canvas and output dimensions must be positive.");
        if (FpsNumerator <= 0 || FpsDenominator <= 0)
            throw new InvalidDataException("Frame-rate numerator and denominator must be positive.");
        if (VideoBitrateKbps <= 0 || AudioBitrateKbps <= 0 || AudioSampleRate <= 0)
            throw new InvalidDataException("Bitrates and sample rate must be positive.");
        if (!double.IsFinite(KeyframeSeconds) || KeyframeSeconds < 0 || BFrames < 0 || SrtLatencyMs < 0)
            throw new InvalidDataException("Keyframe interval, B-frames and SRT latency cannot be negative.");
        // Codec constraints rather than product limits: 4:2:0 video needs even sizes, AAC defines its own sample rates.
        if (OutputWidth % 2 != 0 || OutputHeight % 2 != 0)
            throw new InvalidDataException("Output width and height must be even numbers for H.264/HEVC 4:2:0 video.");
        if (!AacSampleRates.Contains(AudioSampleRate))
            throw new InvalidDataException($"AAC supports these sample rates: {string.Join(", ", AacSampleRates)} Hz.");
    }

    public static readonly int[] AacSampleRates = [8000, 11025, 12000, 16000, 22050, 24000, 32000, 44100, 48000, 64000, 88200, 96000];
}

public sealed class ProjectDocument
{
    public int SchemaVersion { get; set; } = 1;
    public string Name { get; set; } = "My broadcast";
    public string SelectedSceneId { get; set; } = "";
    public List<SourceDefinition> Sources { get; set; } = [];
    public List<Scene> Scenes { get; set; } = [];
    public OutputSettings Output { get; set; } = new();
    [JsonIgnore] public Scene SelectedScene => Scenes.First(s => s.Id == SelectedSceneId);
    public SourceDefinition SourceFor(SceneItem item) => Sources.First(s => s.Id == item.SourceId);
    public string Serialize() => JsonSerializer.Serialize(this, ProjectJsonContext.Default.ProjectDocument);
    public static ProjectDocument Parse(string json)
    {
        var document = JsonSerializer.Deserialize(json, ProjectJsonContext.Default.ProjectDocument)
            ?? throw new InvalidDataException("The scene collection is empty.");
        document.Validate();
        return document;
    }

    public void Validate()
    {
        if (SchemaVersion != 1) throw new InvalidDataException("This scene collection uses an unsupported version.");
        if (Scenes is null || Sources is null || Output is null ||
            Scenes.Any(s => s is null || s.Items is null || s.Items.Any(i => i is null)) || Sources.Any(s => s is null))
            throw new InvalidDataException("The collection must include its scenes, sources and output settings.");
        if (Scenes.Count == 0) throw new InvalidDataException("Keep at least one scene in the collection.");
        if (Scenes.Any(s => string.IsNullOrWhiteSpace(s.Id)) || Sources.Any(s => string.IsNullOrWhiteSpace(s.Id)))
            throw new InvalidDataException("Scene and source identifiers cannot be empty.");
        if (!Scenes.Any(s => s.Id == SelectedSceneId)) throw new InvalidDataException("The selected scene does not exist.");
        if (Scenes.Select(s => s.Id).Distinct().Count() != Scenes.Count ||
            Sources.Select(s => s.Id).Distinct().Count() != Sources.Count)
            throw new InvalidDataException("Scene and source identifiers must be unique.");
        foreach (var source in Sources)
        {
            if (!Enum.IsDefined(source.Kind)) throw new InvalidDataException("The collection includes an unsupported source type.");
            if (!double.IsFinite(source.FontSize) || source.FontSize <= 0)
                throw new InvalidDataException("Text size must be positive.");
        }
        foreach (var scene in Scenes)
        {
            if (scene.Items.Select(i => i.Id).Distinct().Count() != scene.Items.Count)
                throw new InvalidDataException("Scene item identifiers must be unique.");
            foreach (var item in scene.Items)
            {
                if (item is null || string.IsNullOrWhiteSpace(item.Id)) throw new InvalidDataException("Scene items must have identifiers.");
                if (!Sources.Any(s => s.Id == item.SourceId)) throw new InvalidDataException("A scene references a missing source.");
                if (!double.IsFinite(item.X) || !double.IsFinite(item.Y) ||
                    !double.IsFinite(item.Width) || !double.IsFinite(item.Height) || item.Width <= 0 || item.Height <= 0 ||
                    !double.IsFinite(item.Opacity) || item.Opacity < 0 || item.Opacity > 1)
                    throw new InvalidDataException("Source transforms must be finite with positive sizes and opacity between zero and one.");
            }
        }
        Output.Validate();
    }

    public static ProjectDocument CreateDefault()
    {
        var camera = new SourceDefinition { Name = "Back camera", Kind = SourceKind.Camera };
        var title = new SourceDefinition { Name = "Stream title", Kind = SourceKind.Text, Content = "A different point of view.", FontSize = 78 };
        var tag = new SourceDefinition { Name = "Channel tag", Kind = SourceKind.Text, Content = "DEVE / LIVE", Color = "#5AE0C2", FontSize = 34 };
        var bar = new SourceDefinition { Name = "Lower third", Kind = SourceKind.Color, Color = "#101B24" };
        var background = new SourceDefinition { Name = "Starting backdrop", Kind = SourceKind.Color, Color = "#122B32" };
        var starting = new SourceDefinition { Name = "Starting message", Kind = SourceKind.Text, Content = "Starting soon.", FontSize = 140 };
        var screen = new SourceDefinition { Name = "Device screen", Kind = SourceKind.Screen };
        var cameraScene = new Scene { Name = "Camera", Items = [
            new() { SourceId = camera.Id },
            new() { SourceId = bar.Id, X = 0, Y = .74, Width = 1, Height = .26, Opacity = .92 },
            new() { SourceId = tag.Id, X = .045, Y = .78, Width = .8, Height = .055 },
            new() { SourceId = title.Id, X = .045, Y = .855, Width = .91, Height = .1 }
        ] };
        var startingScene = new Scene { Name = "Starting soon", Items = [
            new() { SourceId = background.Id },
            new() { SourceId = tag.Id, X = .08, Y = .24, Width = .84, Height = .08 },
            new() { SourceId = starting.Id, X = .08, Y = .4, Width = .84, Height = .23 }
        ] };
        var screenScene = new Scene { Name = "Screen share", Items = [new() { SourceId = screen.Id }] };
        return new ProjectDocument { Sources = [camera, title, tag, bar, background, starting, screen],
            Scenes = [cameraScene, screenScene, startingScene], SelectedSceneId = cameraScene.Id };
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(ProjectDocument))]
[JsonSerializable(typeof(Scene))]
[JsonSerializable(typeof(OutputSettings))]
public partial class ProjectJsonContext : JsonSerializerContext;
