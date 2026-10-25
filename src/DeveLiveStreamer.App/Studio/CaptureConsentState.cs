namespace DeveLiveStreamer.App.Studio;

/// <summary>Requests consent once per source activation, allowing a retry after leaving and returning to it.</summary>
public sealed class CaptureConsentState
{
    private bool cameraAttempted, screenAttempted, deviceAudioAttempted;
    public bool UsesCamera { get; private set; }
    public bool UsesScreen { get; private set; }

    public void Update(RenderScene scene)
    {
        UsesCamera = scene.Layers.Any(layer => layer.Kind == Core.SourceKind.Camera);
        UsesScreen = scene.Layers.Any(layer => layer.Kind == Core.SourceKind.Screen);
        if (!UsesCamera) cameraAttempted = false;
        if (!UsesScreen) screenAttempted = false;
    }

    public bool RequestCamera(bool permitted)
    {
        if (!UsesCamera || permitted || cameraAttempted) return false;
        cameraAttempted = true;
        return true;
    }

    public bool RequestProjection(bool hasProjection, bool deviceAudio)
    {
        if (hasProjection || !((UsesScreen && !screenAttempted) || (deviceAudio && !deviceAudioAttempted))) return false;
        screenAttempted |= UsesScreen;
        deviceAudioAttempted |= deviceAudio;
        return true;
    }

    public void ProjectionStopped(bool deviceAudio)
    {
        screenAttempted |= UsesScreen;
        deviceAudioAttempted |= deviceAudio;
    }
}
