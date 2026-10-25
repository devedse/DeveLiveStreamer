using DeveLiveStreamer.Core;

namespace DeveLiveStreamer.App.Studio;

public enum LiveState { Offline, Starting, Connecting, Live, Reconnecting, Stopping }

/// <summary>What the UI shows about the output session; refreshed about once a second while streaming.</summary>
public sealed record StudioStatus(LiveState State, string Message, TimeSpan Duration, double BitrateKbps, double RenderFps,
    double EncodeFps, long DroppedFrames, double? RttMs, string Destination, IReadOnlyList<string> Warnings)
{
    public static readonly StudioStatus Offline = new(LiveState.Offline, "", TimeSpan.Zero, 0, 0, 0, 0, null, "", []);
    public bool IsActive => State is not (LiveState.Offline or LiveState.Stopping);
}

/// <summary>The native capture/compositor/encoder/transport engine behind the shared editor.</summary>
public interface IStudioEngine
{
    bool IsSupported { get; }
    StudioStatus Status { get; }
    event EventHandler? StatusChanged;
    /// <summary>Raised when a source's live state (camera busy, network publisher connected, …) changes.</summary>
    event EventHandler? SourcesChanged;
    void UpdateScene(ProjectDocument project);
    Task StartAsync(ProjectDocument project);
    Task StopAsync();
    bool CameraPermitted { get; }
    Task<bool> RequestCameraAsync();
    /// <summary>A short status for a source in the program scene, or null when it is simply working.</summary>
    string? SourceStatus(string sourceId);
    IReadOnlyList<string> LocalAddresses();
}

public static class StudioHost
{
    public static IStudioEngine Engine { get; set; } = new UnsupportedStudioEngine();
}

internal sealed class UnsupportedStudioEngine : IStudioEngine
{
    public bool IsSupported => false;
    public StudioStatus Status => StudioStatus.Offline;
    public event EventHandler? StatusChanged { add { } remove { } }
    public event EventHandler? SourcesChanged { add { } remove { } }
    public void UpdateScene(ProjectDocument project) { }
    public Task StartAsync(ProjectDocument project) => throw new PlatformNotSupportedException("Live streaming is currently implemented for Android.");
    public Task StopAsync() => Task.CompletedTask;
    public bool CameraPermitted => false;
    public Task<bool> RequestCameraAsync() => Task.FromResult(false);
    public string? SourceStatus(string sourceId) => null;
    public IReadOnlyList<string> LocalAddresses() => [];
}
