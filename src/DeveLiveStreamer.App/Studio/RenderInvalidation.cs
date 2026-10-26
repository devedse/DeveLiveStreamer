namespace DeveLiveStreamer.App.Studio;

/// <summary>Completing an older frame must not clear redraw requests made while it was rendering.</summary>
public sealed class RenderInvalidation
{
    private long requested = 1, presented;
    public long Version => Volatile.Read(ref requested);
    public bool NeedsRender => Version != Volatile.Read(ref presented);
    public void Request() => Interlocked.Increment(ref requested);
    public void Presented(long version) => Volatile.Write(ref presented, version);
}
