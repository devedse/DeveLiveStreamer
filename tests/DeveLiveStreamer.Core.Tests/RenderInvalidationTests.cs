using DeveLiveStreamer.App.Studio;

namespace DeveLiveStreamer.Core.Tests;

public sealed class RenderInvalidationTests
{
    [Fact]
    public void SceneChangeDuringRenderingSurvivesPresentationOfTheOlderFrame()
    {
        var invalidation = new RenderInvalidation();
        var inFlight = invalidation.Version;
        invalidation.Request(); // UI layout or scene changes after the renderer took its snapshot.
        invalidation.Presented(inFlight);
        Assert.True(invalidation.NeedsRender);
        invalidation.Presented(invalidation.Version);
        Assert.False(invalidation.NeedsRender);
    }
}
