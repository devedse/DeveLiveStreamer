using DeveLiveStreamer.App.Studio;
using DeveLiveStreamer.Core;

namespace DeveLiveStreamer.Core.Tests;

public sealed class CaptureConsentTests
{
    [Fact]
    public void DefaultCameraSceneDoesNotRequestProjectionForAnotherScenesScreen()
    {
        var state = new CaptureConsentState();
        state.Update(RenderScene.From(ProjectDocument.CreateDefault()));
        Assert.True(state.UsesCamera);
        Assert.False(state.UsesScreen);
        Assert.False(state.RequestProjection(false, false));
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    public void HiddenOrTransparentScreenDoesNotRequestProjection(bool visible, double opacity)
    {
        var project = ProjectDocument.CreateDefault();
        project.SelectedSceneId = project.Scenes.Single(s => s.Name == "Screen share").Id;
        project.SelectedScene.Items[0].Visible = visible;
        project.SelectedScene.Items[0].Opacity = opacity;
        var state = new CaptureConsentState();
        state.Update(RenderScene.From(project));
        Assert.False(state.RequestProjection(false, false));
    }

    [Fact]
    public void DeclinedScreenRequestIsRetriedOnlyAfterReactivation()
    {
        var project = ProjectDocument.CreateDefault();
        var camera = RenderScene.From(project);
        project.SelectedSceneId = project.Scenes.Single(s => s.Name == "Screen share").Id;
        var screen = RenderScene.From(project);
        var state = new CaptureConsentState();
        state.Update(screen);
        Assert.True(state.RequestProjection(false, false));
        state.Update(screen); // A drag or redraw must not show the same dialog again.
        Assert.False(state.RequestProjection(false, false));
        state.Update(camera);
        Assert.False(state.RequestProjection(false, false));
        state.Update(screen);
        Assert.True(state.RequestProjection(false, false));
    }

    [Fact]
    public void DeviceAudioRequestsProjectionWithoutScreenAndDoesNotRepeatAfterDenial()
    {
        var state = new CaptureConsentState();
        state.Update(RenderScene.From(ProjectDocument.CreateDefault()));
        Assert.True(state.RequestProjection(false, true));
        Assert.False(state.RequestProjection(false, true));
    }

    [Fact]
    public void GrantedOrRevokedProjectionDoesNotPromptDuringRedraws()
    {
        var project = ProjectDocument.CreateDefault();
        project.SelectedSceneId = project.Scenes.Single(s => s.Name == "Screen share").Id;
        var state = new CaptureConsentState();
        state.Update(RenderScene.From(project));
        Assert.False(state.RequestProjection(true, false));
        state.ProjectionStopped(false);
        Assert.False(state.RequestProjection(false, false));
    }

    [Fact]
    public void CameraPermissionFollowsActiveSceneAndIsRequestedOncePerActivation()
    {
        var project = ProjectDocument.CreateDefault();
        var camera = RenderScene.From(project);
        project.SelectedSceneId = project.Scenes.Single(s => s.Name == "Screen share").Id;
        var state = new CaptureConsentState();
        state.Update(RenderScene.From(project));
        Assert.False(state.RequestCamera(false));
        state.Update(camera);
        Assert.True(state.RequestCamera(false));
        Assert.False(state.RequestCamera(false));
        Assert.False(state.RequestCamera(true));
    }
}
